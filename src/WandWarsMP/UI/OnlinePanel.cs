using UnityEngine;
using WandWarsMP.Net;

namespace WandWarsMP.UI
{
    /// <summary>
    /// Advanced F2 overlay (hidden by default): direct-IP host/join for LAN or port-forwarded play,
    /// connection status and pings. The normal flow is the native "online" main-menu entry.
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

        private int ParsePort() => int.TryParse(port, out int p) && p > 0 && p < 65536 ? p : Protocol.DefaultPort;
    }
}
