using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.UI
{
    /// <summary>
    /// Small round-trip ping readout in the top-left corner during online matches (debugging aid).
    /// Clients show their ping to the host; the host shows the worst ping among online players.
    /// </summary>
    public sealed class PingOverlay : MonoBehaviour
    {
        private GUIStyle style, shadow;
        private string text = "";
        private Color color = Color.white;
        private float nextRefresh;

        private void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;
            text = "";
            var net = NetSession.Instance;
            if (!Plugin.ShowPing.Value || net == null || !net.IsConnected || !(Game.State is MatchBase)) return;
            float worst = -1;
            foreach (var rp in net.Remotes.Values) worst = Mathf.Max(worst, net.GetRtt(rp.Peer));
            if (net.Role == NetRole.Client) worst = net.GetRtt(0);
            if (worst < 0) return;
            text = Mathf.RoundToInt(worst) + " ms";
            color = worst < 60 ? new Color(0.6f, 1f, 0.6f) : worst < 120 ? new Color(1f, 0.9f, 0.5f) : new Color(1f, 0.5f, 0.5f);
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(text)) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                shadow = new GUIStyle(style);
                shadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            }
            int size = Mathf.Max(10, Screen.height / 70);
            style.fontSize = shadow.fontSize = size;
            style.normal.textColor = color;
            var r = new Rect(6, 4, size * 8, size * 2);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, shadow);
            GUI.Label(r, text, style);
        }
    }
}
