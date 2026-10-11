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

        public static void TryStart(GameObject host)
        {
            string path = Path.Combine(Paths.GameRootPath, "wwmp_autotest.txt");
            if (!File.Exists(path)) { Plugin.Trace("DevHarness: no " + path); return; }
            var h = host.AddComponent<DevHarness>();
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
            Plugin.Trace($"DevHarness: lobby reached after {Time.realtimeSinceStartup - t0:F1}s");
            yield return new WaitForSeconds(2f);

            switch (Get("mode", "census"))
            {
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
                case "lobbyhost":
                    yield return StartCoroutine(LobbyHostScenario());
                    break;
                case "lobbyclient":
                    yield return StartCoroutine(LobbyClientScenario());
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
                players.Add(new Player(chars[i % chars.Count], (PlayerNumber)i, human ? humans[i] : null, null, isBot: !human));
            }
            Plugin.Trace($"DevHarness: starting Deathmatch on {stage}, {total} players ({humans?.Count ?? 0} human)");
            // Same path as the real menu flow, so sync hooks (CreateMatch postfix) fire.
            Game.GameStateMachine.Start(GameLobby.CreateMatch(stage, players, new MatchConfig { MatchType = MatchType.Deathmatch }));
            while (!(Game.State is MatchBase)) yield return null;
        }

        private IEnumerator NetHostScenario()
        {
            var net = Net.NetSession.Ensure();
            net.StartHost(int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            float deadline = Time.realtimeSinceStartup + 30f;
            while (net.Remotes.Count == 0 && Time.realtimeSinceStartup < deadline) yield return null;
            if (net.Remotes.Count == 0) { Plugin.Trace("DevHarness: no client joined"); yield break; }

            var remote = net.Remotes.Values.First();
            yield return StartCoroutine(StartBotMatch(int.Parse(Get("players", "2")), new List<InControl.InputDevice> { remote.Device }));
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
                    (x.PrimaryEntity != null ? $"P{(int)x.PlayerNumber + 1}{(Vector2)x.PrimaryEntity.transform.position}" : $"P{(int)x.PlayerNumber + 1}-") + $"[{x.Score.Value}]").ToArray());
                Plugin.Trace($"DevHarness: host round={match.RoundNumber} rtt={Net.NetSession.Instance.GetRtt(remote.Peer):F1}ms {ents}");
            }
            Plugin.Trace($"DevHarness: RESULT remote max displacement {maxDist:F2}, hexes cast {p.HexesCast}");
        }

        private static string StateName() => Game.State == null ? "null" : Game.State.GetType().Name;

        /// <summary>Full flow: lobby -> ready -> match to score limit -> back to lobby, N times.</summary>
        private IEnumerator LobbyHostScenario()
        {
            var net = Net.NetSession.Ensure();
            net.StartHost(int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            var lobby = Sync.OnlineLobby.Instance;
            int matches = int.Parse(Get("matches", "2"));
            float deadline = Time.realtimeSinceStartup + float.Parse(Get("seconds", "300"));
            string lastState = null;
            for (int m = 0; m < matches && Time.realtimeSinceStartup < deadline; m++)
            {
                lobby.ScoreLimit = int.Parse(Get("score", "2"));
                lobby.Bots = int.Parse(Get("bots", "1"));
                lobby.MarkDirty();
                while ((!lobby.CanStart || net.Remotes.Count == 0) && Time.realtimeSinceStartup < deadline) yield return null;
                if (!lobby.CanStart) { Plugin.Trace("DevHarness: lobby never ready"); yield break; }
                Plugin.Trace($"DevHarness: lobby ready, starting match {m + 1}");
                lobby.StartMatch();
                while (!(Game.State is MatchBase)) yield return null;
                while (!(Game.State is GameLobby) && Time.realtimeSinceStartup < deadline)
                {
                    if (StateName() != lastState) { lastState = StateName(); Plugin.Trace("DevHarness: host state " + lastState); }
                    var mb = Game.Match;
                    if (mb != null && mb.MatchState.ToString() != lastMatchState) { lastMatchState = mb.MatchState.ToString(); Plugin.Trace("DevHarness: host matchState " + lastMatchState); }
                    yield return null;
                }
                Plugin.Trace($"DevHarness: host back in lobby after match {m + 1} (scores were logged by sync)");
                lastState = null;
                yield return new WaitForSeconds(2f);
            }
            Plugin.Trace("DevHarness: RESULT host completed matches");
        }

        private string lastMatchState;

        private IEnumerator LobbyClientScenario()
        {
            var net = Net.NetSession.Ensure();
            net.LocalName = "TestClient";
            net.Capture.Script = t => new Net.ScriptedInput
            {
                Move = new Vector2(Mathf.Cos(t), Mathf.Sin(t)),
                Buttons = (t % 1f) < 0.1f ? Net.InputState.A : 0,
            };
            net.StartClient(Get("connect", "127.0.0.1"), int.Parse(Get("port", Net.Protocol.DefaultPort.ToString())));
            var lobby = Sync.OnlineLobby.Instance;
            float end = Time.realtimeSinceStartup + float.Parse(Get("seconds", "300"));
            string lastState = null;
            float nextPick = 0;
            while (Time.realtimeSinceStartup < end && net.Role != Net.NetRole.None)
            {
                if (StateName() != lastState) { lastState = StateName(); Plugin.Trace("DevHarness: client state " + lastState); }
                var mb = Game.Match;
                if (mb != null && mb.MatchState.ToString() != lastMatchState) { lastMatchState = mb.MatchState.ToString(); Plugin.Trace("DevHarness: client matchState " + lastMatchState); }
                var me = lobby.LocalSlot;
                if (lobby.InMenu && me != null && !me.Ready && Time.realtimeSinceStartup > nextPick)
                {
                    nextPick = Time.realtimeSinceStartup + 1f;
                    lobby.SendPick(0, true);
                    Plugin.Trace("DevHarness: client ready");
                }
                yield return null;
            }
            Plugin.Trace($"DevHarness: client done, status={net.Status}");
        }

        private IEnumerator NetClientScenario()
        {
            var net = Net.NetSession.Ensure();
            net.LocalName = "TestClient";
            // Circle movement, tap A once a second.
            net.Capture.Script = t => new Net.ScriptedInput
            {
                Move = new Vector2(Mathf.Cos(t), Mathf.Sin(t)),
                Buttons = (t % 1f) < 0.1f ? Net.InputState.A : 0,
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
                    (p.PrimaryEntity != null ? $"P{(int)p.PlayerNumber + 1}{(Vector2)p.PrimaryEntity.transform.position}" : $"P{(int)p.PlayerNumber + 1}-") + $"[{p.Score.Value}]").ToArray());
                Plugin.Trace($"DevHarness: client round={Game.Match.RoundNumber} rtt={net.HostRttMs:F0} avgCorr={sync.AvgCorrection:F3} ownCorr={sync.OwnAvgCorrection:F3} align[-2..3]={string.Join("/", sync.AlignErr.Select(x => x.ToString("F2")).ToArray())} snaps={sync.Snaps} {ents}");
            }
            Plugin.Trace($"DevHarness: client done, status={net.Status} rtt={net.HostRttMs:F1}ms");
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
