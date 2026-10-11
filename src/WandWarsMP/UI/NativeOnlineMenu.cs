using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.UI
{
    /// <summary>
    /// Native "online" entry on the main-menu carousel. Selecting it swaps the carousel to an online
    /// submenu built from cloned menu buttons; each button's description line doubles as live status.
    ///   offline:    host game | join game | back
    ///   hosting:    start | invite friends | leave
    ///   client:     waiting | leave
    /// "start" enters the game's own versus flow (character select -> mode -> stage), which MenuSync
    /// mirrors onto clients. Rebuilt every time the lobby scene loads (all menu objects are scene objects).
    /// </summary>
    public sealed class NativeOnlineMenu : MonoBehaviour
    {
        public static NativeOnlineMenu Instance { get; private set; }

        private MainMenuPanel mm;
        private RectTransformCarousel carousel;
        private MenuButton template;
        private MenuButton onlineButton;
        private List<MenuButton> mainButtons;
        private readonly Dictionary<string, MenuButton> items = new Dictionary<string, MenuButton>();
        private string[] currentKeys;
        private bool inSubmenu;
        private NetRole lastRole;
        private MenuActions actions;

        public bool InSubmenu => inSubmenu;

        public static void Ensure()
        {
            if (Instance == null) Instance = Plugin.Root.AddComponent<NativeOnlineMenu>();
        }

        private void Awake()
        {
            actions = new MenuActions();
            actions.CreateDefaultBindings();
        }

        private void OnDestroy() => actions?.Destroy();

        /// <summary>Called when the lobby scene's GameLobby state starts (main menu objects exist).</summary>
        public void OnLobbyStart()
        {
            mm = Object.FindObjectOfType<MainMenuPanel>();
            if (mm == null) mm = Resources.FindObjectsOfTypeAll<MainMenuPanel>().FirstOrDefault(x => x.gameObject.scene.IsValid());
            if (mm == null) { Plugin.Trace("NativeMenu: no MainMenuPanel"); return; }
            carousel = mm.MainMenuCarousel;
            inSubmenu = false;
            lastRole = NetSession.Instance != null ? NetSession.Instance.Role : NetRole.None;
            items.Clear();
            currentKeys = null;

            var list = Buttons();
            template = list.FirstOrDefault(b => b.ButtonText != null && b.ButtonText.text.Trim().ToLower() == "versus");
            if (template == null) { Plugin.Trace("NativeMenu: versus button not found"); return; }

            onlineButton = CloneButton("online", "play with friends", EnterSubmenu);
            onlineButton.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            list.Insert(list.IndexOf(template) + 1, onlineButton);
            Graphics()[onlineButton] = onlineButton.GetComponentsInChildren<CanvasGroup>(true)[0];
            mainButtons = new List<MenuButton>(list);

            Plugin.Trace("NativeMenu: online entry added");
            if (NetSession.Instance != null && NetSession.Instance.Role != NetRole.None)
                StartCoroutine(OpenWhenMenuReady());
        }

        private IEnumerator OpenWhenMenuReady()
        {
            while (mm != null && GameLobby.CurrentPanel != mm) yield return null;
            yield return new WaitForSeconds(0.2f);
            if (mm != null && !inSubmenu) EnterSubmenu();
        }

        private List<MenuButton> Buttons() => Traverse.Create(carousel).Field("buttons").GetValue<List<MenuButton>>();
        private Dictionary<MenuButton, CanvasGroup> Graphics() =>
            Traverse.Create(carousel).Field("selectableGraphics").GetValue<Dictionary<MenuButton, CanvasGroup>>();

        private MenuButton CloneButton(string label, string description, UnityAction onSelect)
        {
            var go = Object.Instantiate(template.gameObject);
            go.transform.SetParent(template.transform.parent, false);
            go.name = "wwmp_" + label.Replace(' ', '_');
            go.SetActive(true);
            var b = go.GetComponent<MenuButton>();
            b.OnSelect = new UnityEvent();
            b.OnSelect.AddListener(onSelect);
            SetLabel(b, label, description);
            return b;
        }

        private static void SetLabel(MenuButton b, string label, string description)
        {
            if (b.ButtonText != null && b.ButtonText.text != label) b.ButtonText.text = label;
            var desc = b.transform.Find("description");
            var t = desc != null ? desc.GetComponent<Text>() : null;
            if (t != null && t.text != description) t.text = description;
        }

        /// <summary>Replace the carousel's item list (others are hidden) and select <paramref name="select"/>.</summary>
        private void SetCarousel(List<MenuButton> list, MenuButton select)
        {
            foreach (Transform child in template.transform.parent)
            {
                var b = child.GetComponent<MenuButton>();
                if (b != null) child.gameObject.SetActive(list.Contains(b));
            }
            var buttons = Buttons();
            var graphics = Graphics();
            buttons.Clear();
            graphics.Clear();
            foreach (var b in list)
            {
                buttons.Add(b);
                graphics[b] = b.GetComponentsInChildren<CanvasGroup>(true)[0];
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)template.transform.parent);
            carousel.SetItemInstantly(select ?? list[0]);
        }

        // ================= submenu =================

        private void EnterSubmenu()
        {
            if (inSubmenu || carousel == null) return;
            inSubmenu = true;
            currentKeys = null;
            Refresh();
        }

        public void ExitSubmenu()
        {
            if (!inSubmenu || carousel == null) return;
            if (Dev.DevMode.Enabled) Plugin.Trace("NativeMenu: exit submenu " + System.Environment.StackTrace);
            inSubmenu = false;
            SetCarousel(mainButtons, onlineButton);
        }

        private MenuButton Item(string key, string label, string desc, UnityAction action)
        {
            if (!items.TryGetValue(key, out var b) || b == null)
            {
                b = CloneButton(label, desc, action);
                items[key] = b;
            }
            SetLabel(b, label, desc);
            return b;
        }

        private void Refresh()
        {
            var net = NetSession.Ensure();
            var keys = new List<string>();
            var list = new List<MenuButton>();
            void Add(string key, string label, string desc, UnityAction a) { keys.Add(key); list.Add(Item(key, label, desc, a)); }

            if (net.Role == NetRole.None)
            {
                bool steam = Net.SteamLobby.Instance != null;
                Add("host", "host game", steam ? "via steam" : "udp port " + Protocol.DefaultPort, HostGame);
                Add("join", "join game", steam ? "steam friends" : "no steam", JoinGame);
                Add("back", "back", ShortStatus(net), ExitSubmenu);
            }
            else if (net.Role == NetRole.Host)
            {
                int n = net.Remotes.Count + 1;
                Add("start", "start", n == 1 ? "just you so far" : $"{n} players", StartVersus);
                if (Net.SteamLobby.Instance != null && Net.SteamLobby.Instance.InLobby)
                    Add("invite", "invite friends", "steam overlay", () => Net.SteamLobby.Instance.Invite());
                Add("leave", "leave", "close lobby", () => net.Stop());
            }
            else
            {
                Add("wait", net.IsConnected ? "connected" : "connecting", ShortStatus(net), () => { });
                Add("leave", "leave", "disconnect", () => net.Stop());
            }

            if (currentKeys == null || !currentKeys.SequenceEqual(keys))
            {
                if (Dev.DevMode.Enabled) Plugin.Trace("NativeMenu: submenu " + string.Join(",", keys.ToArray()));
                string selectedKey = currentKeys != null && carousel.CurrentItemIndex < currentKeys.Length ? currentKeys[carousel.CurrentItemIndex] : null;
                currentKeys = keys.ToArray();
                int idx = selectedKey != null ? keys.IndexOf(selectedKey) : -1;
                SetCarousel(list, idx >= 0 ? list[idx] : list[0]);
            }
        }

        private static string ShortStatus(NetSession net)
        {
            if (net.Role == NetRole.Client && net.IsConnected)
                return net.HostRttMs >= 0 ? $"ping {net.HostRttMs:F0}ms" : "waiting for host";
            if (net.Role == NetRole.Client) return "please wait";
            return "";
        }

        private void Update()
        {
            if (mm == null || carousel == null) return;
            var net = NetSession.Instance;
            bool atMenu = GameLobby.CurrentPanel == mm;

            // Auto-open when going online from the main menu (e.g. after accepting a Steam invite).
            var role = net != null ? net.Role : NetRole.None;
            if (role != lastRole && role != NetRole.None && !inSubmenu && atMenu) EnterSubmenu();
            lastRole = role;

            if (!inSubmenu) return;
            if (!atMenu) return;
            if (actions.Back.WasPressed) { ExitSubmenu(); return; }
            Refresh();
        }

        // ================= actions =================

        private static void HostGame()
        {
            if (Net.SteamLobby.Instance != null) Net.SteamLobby.Instance.Host();
            else NetSession.Ensure().StartHost(Protocol.DefaultPort);
        }

        private static void JoinGame()
        {
            if (Net.SteamLobby.Instance != null) Steamworks.SteamFriends.ActivateGameOverlay("friends");
        }

        /// <summary>Host: into the game's own versus flow, Arcane Arena preselected.</summary>
        public void StartVersus()
        {
            if (mm == null || NetSession.Instance == null || NetSession.Instance.Role != NetRole.Host) return;
            GameLobby.MatchConfig.MatchType = MatchType.Deathmatch;
            ExitSubmenu();
            mm.PlayerVersus();
            mm.Next();
        }
    }

    [HarmonyPatch(typeof(GameLobby), nameof(GameLobby.Start))]
    internal static class GameLobbyStartPatch
    {
        private static void Postfix()
        {
            try { NativeOnlineMenu.Instance?.OnLobbyStart(); }
            catch (System.Exception e) { Plugin.Trace("NativeMenu failed: " + e); }
        }
    }
}
