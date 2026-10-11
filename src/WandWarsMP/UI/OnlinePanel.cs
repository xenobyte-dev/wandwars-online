using UnityEngine;
using WandWarsMP.Net;

namespace WandWarsMP.UI
{
    /// <summary>
    /// F2 overlay: host via Steam lobby (friends join through invites / friends list) or direct IP,
    /// join by IP, connection status and pings. Deliberately plain IMGUI so it works in every game state.
    /// </summary>
    public sealed class OnlinePanel : MonoBehaviour
    {
        public const KeyCode ToggleKey = KeyCode.F2;

        private bool open;
        private string ip = "127.0.0.1";
        private string port = Protocol.DefaultPort.ToString();
        private bool steamInitTried;
        private bool cursorWasVisible;
        private Rect window = new Rect(20, 20, 360, 10);

        private void Update()
        {
            if (!steamInitTried && SteamManagerReady())
            {
                steamInitTried = true;
                SteamLobby.Init();
            }
            if (Input.GetKeyDown(ToggleKey))
            {
                open = !open;
                if (open) { cursorWasVisible = Cursor.visible; Cursor.visible = true; }
                else Cursor.visible = cursorWasVisible;
            }
        }

        private static bool SteamManagerReady()
        {
            try { return SteamManager.Initialized; } catch { return false; }
        }

        private void OnGUI()
        {
            var net = NetSession.Instance;
            if (!open)
            {
                if (net != null && net.Role != NetRole.None)
                    GUI.Label(new Rect(8, Screen.height - 24, 600, 22), $"Online: {net.Status}  {PingText(net)}   [F2]");
                return;
            }
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

            if (net.Role == NetRole.None)
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
            else
            {
                if (net.Role == NetRole.Host)
                {
                    if (SteamLobby.Instance != null && SteamLobby.Instance.InLobby && GUILayout.Button("Invite friends"))
                        SteamLobby.Instance.Invite();
                    GUILayout.Label($"Players connected: {net.Remotes.Count}");
                    foreach (var rp in net.Remotes.Values)
                    {
                        float rtt = net.GetRtt(rp.Peer);
                        GUILayout.Label($"  P{rp.Slot + 1} {rp.Name}  {(rtt >= 0 ? rtt.ToString("F0") + "ms" : "…")}");
                    }
                }
                else
                {
                    GUILayout.Label(PingText(net));
                }
                if (GUILayout.Button("Disconnect")) net.Stop();
            }
            GUILayout.Label("F2 to close");
            GUI.DragWindow();
        }

        private int ParsePort() => int.TryParse(port, out int p) && p > 0 && p < 65536 ? p : Protocol.DefaultPort;
    }
}
