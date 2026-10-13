using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;
using WandWars;

namespace WandWarsMP.Dev
{
    /// <summary>
    /// Headless-ish test driver. Activated by a file "wwmp_autotest.txt" in the game root.
    /// File lines: key=value. mode=census|nethost|netclient, seconds, quit=true|false, stage, bots, port, connect.
    /// </summary>
    public class DevHarness : MonoBehaviour
    {
        private Dictionary<string, string> cfg;

        public static bool Active { get; private set; }

        public static void TryStart(GameObject host)
        {
            string path = Path.Combine(Paths.GameRootPath, "wwmp_autotest.txt");
            if (!File.Exists(path)) { Plugin.Trace("DevHarness: no " + path); return; }
            var h = host.AddComponent<DevHarness>();
            Active = true;
            h.cfg = File.ReadAllLines(path)
                .Select(l => l.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            Plugin.Trace("DevHarness active: " + string.Join(", ", h.cfg.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
            // netsim=latencyMs,jitterMs,lossPercent (one-way, applied to this instance's outgoing packets)
            if (h.cfg.TryGetValue("netsim", out var sim))
            {
                var parts = sim.Split(',');
                Net.UdpTransport.SimLatencyMs = float.Parse(parts[0]);
                if (parts.Length > 1) Net.UdpTransport.SimJitterMs = float.Parse(parts[1]);
                if (parts.Length > 2) Net.UdpTransport.SimLossPercent = float.Parse(parts[2]);
            }
            if (h.cfg.TryGetValue("predict", out var pred)) Sync.MatchSync.Predict = pred == "true";
        }

        private string Get(string key, string def) => cfg.TryGetValue(key, out var v) ? v : def;

        private IEnumerator Start()
        {
            // Wait for the game to reach the main menu.
            float t0 = Time.realtimeSinceStartup;
            string last = null;
            while (Game.GameStateMachine == null || !(Game.State is GameLobby))
            {
                string cur = Game.GameStateMachine == null ? "no-fsm" : (Game.State == null ? "null" : Game.State.GetType().Name);
                if (cur != last) { Plugin.Trace("DevHarness: waiting, state=" + cur); last = cur; }
                yield return null;
            }
            Plugin.Trace($"DevHarness: lobby reached after {Time.realtimeSinceStartup - t0:F1}s; roster unlocked={Game.GlobalResources.GetCharacters().Length} all={Game.GlobalResources.GetCharacters(true).Length}");
            yield return new WaitForSeconds(2f);

            switch (Get("mode", "census"))
            {
                case "watch":
                    yield return StartCoroutine(StartBotMatch(int.Parse(Get("bots", "8")), null));
                    yield return StartCoroutine(Watch(float.Parse(Get("seconds", "240"))));
                    break;
                case "census":
                    yield return StartCoroutine(StartBotMatch(int.Parse(Get("bots", "4")), null));
                    yield return new WaitForSeconds(5f);
                    yield return StartCoroutine(Census(float.Parse(Get("seconds", "20"))));
                    break;
                case "nethost":
                    yield return StartCoroutine(NetHostScenario());
                    break;
                case "netclient":
                    yield return StartCoroutine(NetClientScenario());
                    break;
                case "nativehost":
                    yield return StartCoroutine(NativeHostScenario());
                    break;
                case "nativeclient":
                    yield return StartCoroutine(NativeClientScenario());
                    break;
                case "steamhost":
                    yield return StartCoroutine(WaitMainMenu());
                    Net.SteamLobby.Init();
                    if (Net.SteamLobby.Instance == null) { Plugin.Trace("DevHarness: RESULT steam unavailable"); break; }
                    Net.SteamLobby.Instance.Host();
                    yield return new WaitForSeconds(6f);
                    Plugin.Trace($"DevHarness: RESULT lobby={Net.SteamLobby.Instance.Lobby} inLobby={Net.SteamLobby.Instance.InLobby} status={Net.NetSession.Instance.Status} transport={Net.NetSession.Instance.Transport?.GetType().Name}");
                    Shot("steam_hosting");
                    yield return new WaitForSeconds(1f);
                    break;
                case "menushots":
                    yield return StartCoroutine(MenuShots());
                    break;
                case "uidump":
                    yield return StartCoroutine(UiDump());
                    break;
            }

            if (Get("quit", "true") == "true") Application.Quit();
        }

        private string PickStage()
        {
            string stage = Get("stage", "");
            return stage == "" ? Stages.GetRandomStage() : stage;
        }

        /// <summary>Starts a Deathmatch: optional remote devices take the first slots, bots fill the rest.</summary>
        private IEnumerator StartBotMatch(int total, List<InControl.InputDevice> humans)
        {
            string stage = PickStage();
            var players = new List<Player>();
            var chars = Game.GlobalResources.GetCharacters().ToList();
            for (int i = 0; i < total; i++)
            {
                bool human = humans != null && i < humans.Count;
                players.Add(new Player(chars[i % chars.Count], Sync.EightPlayers.NumberFor(i), human ? humans[i] : null, null, isBot: !human));
            }
            var type = MatchType.Deathmatch;
            try { type = (MatchType)System.Enum.Parse(typeof(MatchType), Get("matchtype", "Deathmatch"), true); }
            catch { Plugin.Trace("DevHarness: unknown matchtype " + Get("matchtype", "")); }
            if (type.IsTeam())
                for (int i = 0; i < players.Count; i++) players[i].Team = Game.GlobalResources.Teams[i % 2];
            Plugin.Trace($"DevHarness: starting {type} on {stage}, {total} players ({humans?.Count ?? 0} human)");
            // Same path as the real menu flow, so sync hooks (CreateMatch postfix) fire.
            var config = new MatchConfig { MatchType = type };
            if (Get("scorelimit", "") != "") config.ScoreLimit = int.Parse(Get("scorelimit", ""));
            Game.GameStateMachine.Start(GameLobby.CreateMatch(stage, players, config));
            while (!(Game.State is MatchBase)) yield return null;
        }

        private IEnumerator NetHostScenario()
        {
            var net = Net.NetSession.Ensure();
            net.StartHost(int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            int needed = int.Parse(Get("remotes", "1"));
            float deadline = Time.realtimeSinceStartup + 45f;
            while (net.Remotes.Count < needed && Time.realtimeSinceStartup < deadline) yield return null;
            if (net.Remotes.Count == 0) { Plugin.Trace("DevHarness: no client joined"); yield break; }
            Plugin.Trace($"DevHarness: {net.Remotes.Count} client(s) joined");

            var remote = net.Remotes.Values.OrderBy(r => r.Slot).First();
            var humans = net.Remotes.Values.OrderBy(r => r.Slot).Select(r => (InControl.InputDevice)r.Device).ToList();
            yield return StartCoroutine(StartBotMatch(int.Parse(Get("players", "2")), humans));
            var match = (MatchBase)Game.State;
            Player p = match.Players.First(x => x.InputDevice == remote.Device);

            float end = Time.realtimeSinceStartup + float.Parse(Get("seconds", "20"));
            Vector3 start = Vector3.zero;
            bool haveStart = false;
            float maxDist = 0;
            while (Time.realtimeSinceStartup < end)
            {
                yield return new WaitForSeconds(1f);
                if (p.PrimaryEntity == null) { Plugin.Trace("DevHarness: remote player has no entity"); continue; }
                Vector3 pos = p.PrimaryEntity.transform.position;
                if (!haveStart) { start = pos; haveStart = true; }
                maxDist = Mathf.Max(maxDist, Vector3.Distance(start, pos));
                string ents = string.Join(" ", match.Players.Select(x =>
                    (x.PrimaryEntity != null ? $"P{(int)x.PlayerNumber + 1}{(Vector2)x.PrimaryEntity.transform.position}" : $"P{(int)x.PlayerNumber + 1}-") + $"[{x.Score.Value}|c{x.ChickensEaten.Value}]").ToArray()) + $" ents={Sync.MatchSync.Instance.NetEntityCount}";
                Plugin.Trace($"DevHarness: host round={match.RoundNumber} rtt={Net.NetSession.Instance.GetRtt(remote.Peer):F1}ms {ents}");
            }
            Plugin.Trace($"DevHarness: RESULT remote max displacement {maxDist:F2}, hexes cast {p.HexesCast}");
            if (Get("abort", "false") == "true")
            {
                Sync.MatchSync.Instance.HostAbortMatch();
                Game.Match.QuitToMainMenu();
                float t = Time.realtimeSinceStartup + 15f;
                while (!(Game.State is GameLobby) && Time.realtimeSinceStartup < t) yield return null;
                Plugin.Trace("DevHarness: RESULT host state after abort: " + StateName());
                yield return new WaitForSeconds(3f);
            }
        }

        /// <summary>Screenshot(s) + hierarchy dump of the lobby scene UI, for building native menus.</summary>
        private IEnumerator UiDump()
        {
            yield return new WaitForSeconds(3f);
            Shot("menu_main");
            var sb = new StringBuilder();
            foreach (var root in FindObjectsOfType<Canvas>().Select(c => c.rootCanvas).Distinct())
                DumpTree(root.transform, 0, sb);
            File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "wwmp_ui.txt"), sb.ToString());
            Plugin.Trace("DevHarness: ui dumped");
            // Optional scripted navigation: nav=Panel1,Panel2 enters panels by name and screenshots each.
            foreach (var name in Get("nav", "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var panel = Resources.FindObjectsOfTypeAll<Panel>().FirstOrDefault(p => p.name == name && p.gameObject.scene.IsValid());
                if (panel == null) { Plugin.Trace("DevHarness: no panel " + name); continue; }
                GameLobby.CurrentPanel.Exit();
                panel.PreviousPanel = GameLobby.CurrentPanel;
                panel.Enter();
                yield return new WaitForSeconds(2.5f);
                Shot("menu_" + name);
            }
            yield return new WaitForSeconds(1f);
        }

        private IEnumerator WaitMainMenu()
        {
            var mm = Resources.FindObjectsOfTypeAll<MainMenuPanel>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            float t = Time.realtimeSinceStartup + 15f;
            while (GameLobby.CurrentPanel != mm && Time.realtimeSinceStartup < t)
            {
                // First run with keyboard only shows a controls screen that waits for a key press.
                if (mm != null && mm.NavigationInstructions != null && mm.NavigationInstructions.gameObject.activeInHierarchy)
                    mm.NavigationInstructions.gameObject.SetActive(false);
                yield return null;
            }
            yield return new WaitForSeconds(1.5f);
        }

        /// <summary>Native flow: online menu -> host -> start -> real character/mode/stage select -> match.</summary>
        private IEnumerator NativeHostScenario()
        {
            yield return StartCoroutine(WaitMainMenu());
            var net = Net.NetSession.Ensure();
            net.StartHost(int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            float deadline = Time.realtimeSinceStartup + float.Parse(Get("seconds", "240"));
            int wantRemotes = int.Parse(Get("remotes", "1"));
            while (net.Remotes.Count < wantRemotes && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSeconds(2f);
            Shot("h1_hosting");
            yield return null;
            UI.NativeOnlineMenu.Instance.StartVersus();
            float csWait = Time.realtimeSinceStartup + 10f;
            while (!(GameLobby.CurrentPanel is CharacterSelection) && Time.realtimeSinceStartup < csWait) yield return null;
            yield return new WaitForSeconds(float.Parse(Get("confirmdelay", "2")));
            // Stand-in for the host pressing confirm on their own box.
            var cs = GameLobby.CurrentPanel as CharacterSelection;
            if (cs != null)
            {
                var boxes = HarmonyLib.Traverse.Create(cs).Field("boxes").GetValue<CharacterSelectionBox[]>();
                if (Get("hostdevice", "") == "keyboard")
                {
                    var kb = InControl.InputManager.Devices.FirstOrDefault(d => d is KeyboardDevice);
                    if (kb != null) boxes[0].SetInputDevice(kb);
                }
                boxes[0].SetState(CharacterSelectionBox.SelectionState.Confirmed);
                // Confirm the online players' boxes too, so a slow client tap can't be left out.
                for (int i = 1; i <= net.Remotes.Count && i < boxes.Length; i++)
                    if (boxes[i].State == CharacterSelectionBox.SelectionState.Active) boxes[i].SetState(CharacterSelectionBox.SelectionState.Confirmed);
                // bots=N: fill the boxes after the humans with N CPU players (clear bots saved earlier).
                for (int i = net.Remotes.Count + 1; i < boxes.Length; i++)
                    if (boxes[i].SelectionConfig.IsBot)
                    {
                        boxes[i].SelectionConfig.Difficulty = -1;
                        boxes[i].SetState(CharacterSelectionBox.SelectionState.Inactive);
                    }
                int bots = int.Parse(Get("bots", "0"));
                for (int i = net.Remotes.Count + 1; i < boxes.Length && bots > 0; i++, bots--)
                {
                    boxes[i].SelectionConfig.Difficulty = 1;
                    boxes[i].SetState(CharacterSelectionBox.SelectionState.Confirmed);
                }
                Plugin.Trace("DevHarness: host confirmed own box; boxes=" + string.Join(",", boxes.Select(b => b.State + ":" + (b.Device != null ? b.Device.Name : "-")).ToArray()));
            }
            string last = null;
            float nextHostProbe = 0f;
            while (Time.realtimeSinceStartup < deadline)
            {
                string cur = Game.State is GameLobby ? (GameLobby.CurrentPanel != null ? GameLobby.CurrentPanel.name : "?") : StateName();
                if (cur != last)
                {
                    last = cur;
                    Plugin.Trace("DevHarness: host at " + cur);
                    Shot("h_" + cur);
                    if (GameLobby.CurrentPanel is ModeSelectPanel ms)
                    {
                        string want = Get("modename", "arcane arena");
                        var b = ms.ModeCarousel.Buttons.FirstOrDefault(x => x.ButtonText != null && x.ButtonText.text.Trim().ToLower() == want);
                        if (b != null) ms.ModeCarousel.SetItem(b);
                        Plugin.Trace("DevHarness: host mode -> " + (b != null ? want : "not found: " + want + " in [" + string.Join("|", ms.ModeCarousel.Buttons.Select(x => x.ButtonText != null ? x.ButtonText.text : x.name).ToArray()) + "]"));
                    }
                }
                if (Game.State is MatchBase mb && mb.MatchState == MatchState.ScoreScreen) break;
                if (Game.State is MatchBase mb2 && Time.realtimeSinceStartup > nextHostProbe)
                {
                    nextHostProbe = Time.realtimeSinceStartup + 1f;
                    var hp = mb2.Players.FirstOrDefault(x => !x.IsBot && !(x.InputDevice is Net.NetInputDevice));
                    var he = hp != null ? hp.PrimaryEntity : null;
                    var dd = hp != null ? hp.InputDevice as Net.DelayedInputDevice : null;
                    Plugin.Trace("DevHarness: host player " + (he != null ? (Vector2)he.transform.position : Vector2.zero)
                        + " device=" + (hp?.InputDevice?.GetType().Name ?? "-") + " attached=" + (hp?.InputDevice?.IsAttached ?? false)
                        + (dd != null ? " source=" + dd.Source.Name + " srcRight=" + dd.Source.GetControl(InControl.InputControlType.LeftStickRight).Value + " delayedRight=" + dd.GetControl(InControl.InputControlType.LeftStickRight).Value : ""));
                }
                yield return null;
            }
            while (!(Game.State is GameLobby) && Time.realtimeSinceStartup < deadline) yield return null;
            Plugin.Trace("DevHarness: RESULT host back in lobby");
            yield return new WaitForSeconds(3f);
            Shot("h_after");
            yield return new WaitForSeconds(1f);
        }

        private IEnumerator NativeClientScenario()
        {
            yield return StartCoroutine(WaitMainMenu());
            var net = Net.NetSession.Ensure();
            net.LocalName = "TestClient";
            // Only taps A (no stick) so it walks the host's menus forward without changing picks.
            net.Capture.Script = t => new Net.ScriptedInput { Buttons = (t % 1.2f) < 0.1f ? Net.InputState.A : 0 };
            net.StartClient(Get("connect", "127.0.0.1"), int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            float end = Time.realtimeSinceStartup + float.Parse(Get("seconds", "250"));
            string last = null;
            yield return new WaitForSeconds(1.5f);
            Shot("c1_connected");
            while (Time.realtimeSinceStartup < end && net.Role != Net.NetRole.None)
            {
                string cur = Game.State is GameLobby ? (GameLobby.CurrentPanel != null ? GameLobby.CurrentPanel.name : "?") : StateName();
                if (cur != last)
                {
                    last = cur;
                    Plugin.Trace("DevHarness: client at " + cur);
                    StartCoroutine(DelayedShot("c_" + cur, 1.2f));
                    StartCoroutine(DelayedShot("c_" + cur + "_b", 3.5f));
                }
                yield return null;
            }
            Plugin.Trace("DevHarness: client done " + net.Status);
        }

        private IEnumerator DelayedShot(string name, float delay)
        {
            yield return new WaitForSeconds(delay);
            Shot(name);
        }

        private IEnumerator MenuShots()
        {
            yield return StartCoroutine(WaitMainMenu());
            var mm = Resources.FindObjectsOfTypeAll<MainMenuPanel>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            var online = mm.MainMenuCarousel.Buttons.FirstOrDefault(b => b.name == "wwmp_online");
            if (online == null) { Plugin.Trace("DevHarness: no online button"); yield break; }
            mm.MainMenuCarousel.SetItem(online);
            yield return new WaitForSeconds(1f);
            Shot("1_main_online");
            yield return null;
            yield return null;
            online.Select();
            yield return new WaitForSeconds(1f);
            Shot("2_submenu");
            yield return null;
            yield return null;
            Net.NetSession.Ensure().StartHost(Net.Protocol.DefaultPort);
            yield return new WaitForSeconds(1.5f);
            Shot("3_hosting");
            yield return null;
            yield return null;
            UI.NativeOnlineMenu.Instance.StartVersus();
            yield return new WaitForSeconds(2.5f);
            Shot("4_charselect");
            yield return new WaitForSeconds(5f);
            Shot("4_charselect_b");
            var csp = GameLobby.CurrentPanel;
            if (csp != null)
            {
                var sb = new StringBuilder();
                for (var t = csp.transform; t != null; t = t.parent)
                    sb.Append(t.name).Append(" scale=").Append(t.localScale).Append(" pos=").Append(t.localPosition).AppendLine();
                DumpTree(csp.transform, 0, sb);
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "wwmp_charselect.txt"), sb.ToString());
            }
            yield return new WaitForSeconds(1f);
        }

        internal static void Shot(string name)
        {
            string path = Path.Combine(Paths.BepInExRootPath, "shots");
            Directory.CreateDirectory(path);
            Application.CaptureScreenshot(Path.Combine(path, name + ".png"));
            Plugin.Trace("DevHarness: screenshot " + name);
        }

        private static void DumpTree(Transform t, int depth, StringBuilder sb)
        {
            var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name).ToArray();
            var text = t.GetComponent<UnityEngine.UI.Text>();
            sb.Append(new string(' ', depth * 2)).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name)
              .Append("  [").Append(string.Join(",", comps)).Append("]");
            if (text != null) sb.Append("  \"").Append(text.text.Replace('\n', ' ')).Append("\"");
            if (t is RectTransform rt) sb.Append($"  pos={rt.anchoredPosition} size={rt.sizeDelta}");
            sb.AppendLine();
            if (depth < 9) foreach (Transform c in t) DumpTree(c, depth + 1, sb);
        }

        private static string StateName() => Game.State == null ? "null" : Game.State.GetType().Name;

        private IEnumerator NetClientScenario()
        {
            var net = Net.NetSession.Ensure();
            net.LocalName = "TestClient";
            bool autoCatch = Get("autocatch", "false") == "true";
            // Circle movement, tap A once a second; walk into a portal when one is open (Spellstorm).
            net.Capture.Script = t =>
            {
                var move = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
                var portal = FindObjectOfType<TrialPortal>();
                var me = Game.Match != null ? Game.Match.Players.FirstOrDefault(p => !p.IsBot && p.HasEntities) : null;
                if (portal != null && me != null && me.Entities[0] != null)
                    move = ((Vector2)(portal.transform.position - me.Entities[0].transform.position)).normalized;
                int buttons = (t % 1f) < 0.1f ? Net.InputState.A : 0;
                if (autoCatch && me != null && me.Entities.Count > 0 && me.Entities[0] != null)
                {
                    // Swing when a ball is close and coming at us (exercises catch prediction).
                    Vector2 mine = me.Entities[0].transform.position;
                    buttons = 0;
                    foreach (var b in Ball.Balls)
                    {
                        if (b == null || b.CurrentCatchSpell != null) continue;
                        Vector2 d = mine - (Vector2)b.transform.position;
                        if (d.magnitude < 3.5f && Vector2.Dot(b.Rigidbody2D.velocity, d) > 0f)
                        {
                            buttons = Net.InputState.A;
                            move = d.normalized * -1f; // face the ball (swing direction = last move)
                        }
                    }
                }
                return new Net.ScriptedInput { Move = move, Buttons = buttons };
            };
            net.StartClient(Get("connect", "127.0.0.1"), int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            float end = Time.realtimeSinceStartup + float.Parse(Get("seconds", "30"));
            float nextLog = 0;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                if (Time.realtimeSinceStartup < nextLog || Game.Match == null) continue;
                nextLog = Time.realtimeSinceStartup + 1f;
                var sync = Sync.MatchSync.Instance;
                string ents = string.Join(" ", Game.Match.Players.Select(p =>
                    (p.PrimaryEntity != null ? $"P{(int)p.PlayerNumber + 1}{(Vector2)p.PrimaryEntity.transform.position}" : $"P{(int)p.PlayerNumber + 1}-") + $"[{p.Score.Value}|c{p.ChickensEaten.Value}]").ToArray()) + $" ents={Sync.MatchSync.Instance.NetEntityCount}";
                Plugin.Trace($"DevHarness: client round={Game.Match.RoundNumber} rtt={net.HostRttMs:F0} avgCorr={sync.AvgCorrection:F3} ownCorr={sync.OwnAvgCorrection:F3} align[-2..3]={string.Join("/", sync.AlignErr.Select(x => x.ToString("F2")).ToArray())} snaps={sync.Snaps} {ents}");
            }
            Plugin.Trace($"DevHarness: client done, status={net.Status} rtt={net.HostRttMs:F1}ms state={StateName()}");
        }

        /// <summary>Screenshots a bot match periodically and its score screen.</summary>
        private IEnumerator Watch(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds, nextShot = Time.realtimeSinceStartup + 12f;
            int n = 0, hudShots = 0;
            StartCoroutine(DelayedShot("watch_vs", 2.5f));
            while (Time.realtimeSinceStartup < end)
            {
                var mb = Game.Match;
                var rsm = mb != null ? HarmonyLib.Traverse.Create(mb).Field("roundScoreModule").GetValue<Component>() : null;
                if (rsm != null && rsm.gameObject.activeInHierarchy && hudShots < 2)
                {
                    yield return new WaitForSeconds(1.5f);
                    Shot("watch_hud" + hudShots++ + "a");
                    yield return new WaitForSeconds(2.5f);
                    Shot("watch_hud" + (hudShots - 1) + "b");
                    while (rsm != null && rsm.gameObject.activeInHierarchy) yield return null;
                    continue;
                }
                if (mb != null && mb.MatchState == MatchState.ScoreScreen)
                {
                    yield return new WaitForSeconds(9f);
                    Shot("watch_score");
                    yield return null;
                    yield break;
                }
                if (Time.realtimeSinceStartup > nextShot && n < 3)
                {
                    nextShot = Time.realtimeSinceStartup + 15f;
                    Shot("watch_" + n++);
                }
                yield return null;
            }
        }

        private IEnumerator Census(float seconds)
        {
            var sb = new StringBuilder();
            var known = new HashSet<int>();
            var spawnedNames = new Dictionary<string, int>();
            int samples = 0, created = 0, destroyed = 0, maxLive = 0;
            float end = Time.realtimeSinceStartup + seconds;

            foreach (var go in FindObjectsOfType<GameObject>()) known.Add(go.GetInstanceID());

            while (Time.realtimeSinceStartup < end)
            {
                var now = FindObjectsOfType<GameObject>();
                var ids = new HashSet<int>();
                foreach (var go in now)
                {
                    int id = go.GetInstanceID();
                    ids.Add(id);
                    if (!known.Contains(id))
                    {
                        created++;
                        string n = go.transform.parent == null ? go.name : go.transform.root.name + "/" + go.name;
                        spawnedNames[n] = spawnedNames.TryGetValue(n, out var c) ? c + 1 : 1;
                    }
                }
                destroyed += known.Count(k => !ids.Contains(k));
                known = ids;
                maxLive = Math.Max(maxLive, now.Length);
                samples++;
                yield return null;
            }

            sb.AppendLine($"Scene: {Application.loadedLevelName}  samples={samples} over {seconds}s");
            sb.AppendLine($"Active GameObjects now={known.Count} max={maxLive} created={created} destroyed={destroyed}");
            sb.AppendLine();

            sb.AppendLine("== Component counts (active) ==");
            foreach (var g in FindObjectsOfType<Component>().GroupBy(c => c.GetType().FullName).OrderByDescending(g => g.Count()))
                sb.AppendLine($"{g.Count(),5}  {g.Key}");
            sb.AppendLine();

            sb.AppendLine("== Renderers (enabled) by type / shader ==");
            foreach (var g in FindObjectsOfType<Renderer>().Where(r => r.enabled)
                         .GroupBy(r => r.GetType().Name + " | " + (r.sharedMaterial ? r.sharedMaterial.shader.name : "null"))
                         .OrderByDescending(g => g.Count()))
                sb.AppendLine($"{g.Count(),5}  {g.Key}");
            sb.AppendLine();

            sb.AppendLine("== Cameras ==");
            foreach (var cam in FindObjectsOfType<Camera>())
                sb.AppendLine($"{cam.name} depth={cam.depth} ortho={cam.orthographic} size={cam.orthographicSize} mask={cam.cullingMask} rt={(cam.targetTexture ? cam.targetTexture.name : "-")}");
            sb.AppendLine();

            sb.AppendLine("== Spawned during sample (by root/name) ==");
            foreach (var kv in spawnedNames.OrderByDescending(k => k.Value).Take(80))
                sb.AppendLine($"{kv.Value,5}  {kv.Key}");
            sb.AppendLine();

            sb.AppendLine("== Hierarchy roots ==");
            foreach (var go in FindObjectsOfType<GameObject>().Where(g => g.transform.parent == null).OrderBy(g => g.name))
                sb.AppendLine($"{go.name}  (children={go.GetComponentsInChildren<Transform>().Length - 1})");

            string outPath = Path.Combine(Paths.BepInExRootPath, "wwmp_census.txt");
            File.WriteAllText(outPath, sb.ToString());
            Plugin.Trace("DevHarness: census written to " + outPath);
        }
    }
}
