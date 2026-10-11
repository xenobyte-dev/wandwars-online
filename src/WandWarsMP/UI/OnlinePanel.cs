using UnityEngine;
using WandWarsMP.Net;
using WandWarsMP.Sync;

namespace WandWarsMP.UI
{
    /// <summary>
    /// F2 overlay: host via Steam lobby (friends join through invites / friends list) or direct IP,
    /// join by IP, connection status and pings, and the pre-match lobby (character, ready, host settings).
    /// Deliberately plain IMGUI so it works in every game state. Opens itself when connected at the menu.
    /// </summary>
    public sealed class OnlinePanel : MonoBehaviour
    {
        public const KeyCode ToggleKey = KeyCode.F2;

        private bool open;
        private string ip = "127.0.0.1";
        private string port = Protocol.DefaultPort.ToString();
        private bool steamInitTried;
        private bool cursorWasVisible;
        private Rect window = new Rect(20, 20, 380, 10);

        private void Update()
        {
            if (!steamInitTried && SteamManagerReady())
            {
                steamInitTried = true;
                SteamLobby.Init();
            }
            if (Input.GetKeyDown(ToggleKey)) SetOpen(!open);

            // Advanced/debug panel only (direct IP, pings). The native "online" menu is the normal flow.
        }

        private void SetOpen(bool value)
        {
            if (open == value) return;
            open = value;
            if (open) { cursorWasVisible = Cursor.visible; Cursor.visible = true; }
            else Cursor.visible = cursorWasVisible;
        }

        private static bool SteamManagerReady()
        {
            try { return SteamManager.Initialized; } catch { return false; }
        }

        private void OnGUI()
        {
            var net = NetSession.Instance;
            if (!open) return;
            window = GUILayout.Window(0x5757, window, DrawWindow, $"Wand Wars Online {Plugin.Version}");
        }

        private static string PingText(NetSession net)
        {
            if (net.Role == NetRole.Client && net.HostRttMs >= 0) return $"ping {net.HostRttMs:F0}ms";
            return "";
        }

        private void DrawWindow(int id)
        {
            var net = NetSession.Ensure();
            GUILayout.Label("Status: " + net.Status);

            if (net.Role == NetRole.None) DrawConnect(net);
            else
            {
                if (net.Role == NetRole.Host && SteamLobby.Instance != null && SteamLobby.Instance.InLobby
                    && GUILayout.Button("Invite friends"))
                    SteamLobby.Instance.Invite();
                if (net.Role == NetRole.Client) GUILayout.Label(PingText(net));
                if (net.IsConnected) DrawLobby(net);
                GUILayout.Space(6);
                if (GUILayout.Button("Disconnect")) net.Stop();
            }
            GUILayout.Label("F2 to show/hide");
            GUI.DragWindow();
        }

        private void DrawConnect(NetSession net)
        {
            GUILayout.Label("Steam (recommended)");
            GUI.enabled = SteamLobby.Instance != null;
            if (GUILayout.Button("Host game + invite friends")) SteamLobby.Instance.Host();
            GUI.enabled = true;
            GUILayout.Label("Friends join from your invite or via Join Game in the Steam friends list.");

            GUILayout.Space(8);
            GUILayout.Label("Direct IP (LAN / port-forwarded)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(40));
            port = GUILayout.TextField(port, GUILayout.Width(70));
            if (GUILayout.Button("Host")) net.StartHost(ParsePort());
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(40));
            ip = GUILayout.TextField(ip, GUILayout.Width(160));
            if (GUILayout.Button("Join")) net.StartClient(ip.Trim(), ParsePort());
            GUILayout.EndHorizontal();
        }

        private void DrawLobby(NetSession net)
        {
            var lobby = OnlineLobby.Instance;
            if (lobby == null || !lobby.HasState) { GUILayout.Label("Waiting for lobby…"); return; }
            if (!lobby.InMenu) { GUILayout.Label("Match in progress."); return; }

            GUILayout.Space(6);
            GUILayout.Label("— Arcane Arena lobby —");
            foreach (var s in lobby.Slots)
            {
                string ch = s.Character == OnlineLobby.RandomPick || s.Character >= lobby.CharacterNames.Count
                    ? "Random" : lobby.CharacterNames[s.Character];
                string ping = "";
                if (net.Role == NetRole.Host && s.Id != 0)
                    foreach (var rp in net.Remotes.Values)
                        if (rp.Slot == s.Id && net.GetRtt(rp.Peer) >= 0) ping = $" {net.GetRtt(rp.Peer):F0}ms";
                GUILayout.Label($"P{s.Id + 1} {s.Name}: {ch}{(s.Id == 0 ? " (host)" : s.Ready ? "  READY" : "  …")}{ping}");
            }
            if (lobby.Bots > 0) GUILayout.Label($"+ {lobby.Bots} bot(s), difficulty {lobby.BotDifficulty}");

            var me = lobby.LocalSlot;
            if (me != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Your wizard:", GUILayout.Width(90));
                if (GUILayout.Button("<", GUILayout.Width(28))) lobby.SendPick(Step(me.Character, -1, lobby.Characters.Count), me.Ready);
                string name = me.Character == OnlineLobby.RandomPick || me.Character >= lobby.CharacterNames.Count
                    ? "Random" : lobby.CharacterNames[me.Character];
                GUILayout.Label(name, GUILayout.Width(110));
                if (GUILayout.Button(">", GUILayout.Width(28))) lobby.SendPick(Step(me.Character, 1, lobby.Characters.Count), me.Ready);
                GUILayout.EndHorizontal();
                if (net.Role == NetRole.Client && GUILayout.Button(me.Ready ? "Not ready" : "Ready"))
                    lobby.SendPick(me.Character, !me.Ready);
            }

            if (net.Role == NetRole.Host)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Stage:", GUILayout.Width(90));
                if (GUILayout.Button("<", GUILayout.Width(28))) { lobby.StageIndex = Wrap(lobby.StageIndex - 1, lobby.Stages.Count); lobby.MarkDirty(); }
                GUILayout.Label(lobby.StageNames[lobby.StageIndex], GUILayout.Width(110));
                if (GUILayout.Button(">", GUILayout.Width(28))) { lobby.StageIndex = Wrap(lobby.StageIndex + 1, lobby.Stages.Count); lobby.MarkDirty(); }
                GUILayout.EndHorizontal();

                Stepper("Rounds to win:", ref lobby.ScoreLimit, 1, 10, lobby);
                Stepper("Bots:", ref lobby.Bots, 0, OnlineLobby.MaxPlayers - lobby.Slots.Count, lobby);
                if (lobby.Bots > 0) Stepper("Bot skill:", ref lobby.BotDifficulty, 0, 100, lobby, 25);

                GUI.enabled = lobby.CanStart;
                if (GUILayout.Button(lobby.CanStart ? "Start match" : "Start match (waiting for players to ready up)"))
                    lobby.StartMatch();
                GUI.enabled = true;
            }
            else GUILayout.Label("The host starts the match.");
        }

        private static void Stepper(string label, ref int value, int min, int max, OnlineLobby lobby, int step = 1)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            if (GUILayout.Button("-", GUILayout.Width(28)) && value > min) { value = Mathf.Max(min, value - step); lobby.MarkDirty(); }
            GUILayout.Label(value.ToString(), GUILayout.Width(110));
            if (GUILayout.Button("+", GUILayout.Width(28)) && value < max) { value = Mathf.Min(max, value + step); lobby.MarkDirty(); }
            GUILayout.EndHorizontal();
        }

        /// <summary>Cycle through characters with "Random" (255) between the last and first entries.</summary>
        private static byte Step(byte current, int delta, int count)
        {
            int idx = current == OnlineLobby.RandomPick ? count : current; // random sits at position count
            idx = Wrap(idx + delta, count + 1);
            return idx == count ? OnlineLobby.RandomPick : (byte)idx;
        }

        private static int Wrap(int v, int n) => ((v % n) + n) % n;

        private int ParsePort() => int.TryParse(port, out int p) && p > 0 && p < 65536 ? p : Protocol.DefaultPort;
    }
}
