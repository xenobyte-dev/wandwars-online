using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Mirrors the host's native versus menus (character select -> mode select -> stage select) onto
    /// clients. The host's screens are the real ones: remote players are extra controllers there.
    /// Clients show the same panels, driven entirely by host state; their own menu input is disabled
    /// (it is streamed to the host instead, see NetSession input).
    /// </summary>
    public sealed class MenuSync : MonoBehaviour
    {
        public static MenuSync Instance { get; private set; }

        /// <summary>Client is showing host-driven menus (local menu input must be off).</summary>
        public static bool ClientMirroring { get; private set; }

        private const float SendInterval = 0.1f, ResendInterval = 1f;

        private NetSession net;
        private readonly NetWriter w = new NetWriter();
        private byte[] lastSent;
        private float nextSend, lastSendTime;

        // client
        private MenuStateData pending;
        private string appliedPanel;

        private sealed class BoxData
        {
            public byte State;
            public string Character;
            public sbyte Skin;
            public sbyte Difficulty;
        }

        private sealed class PlayerData
        {
            public string Character;
            public bool IsBot;
            public int Difficulty;
            public int Team;
        }

        private sealed class MenuStateData
        {
            public string Panel;
            public List<BoxData> Boxes = new List<BoxData>();
            public bool ReadyButton;
            public string Mode, Length, Stage;
            public List<PlayerData> Players = new List<PlayerData>();
        }

        public static void Ensure()
        {
            if (Instance == null) Instance = Plugin.Root.AddComponent<MenuSync>();
        }

        private void Start()
        {
            net = NetSession.Ensure();
            net.Message += OnMessage;
        }

        private static T Find<T>() where T : Component =>
            Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x => x.gameObject.scene.IsValid());

        private static bool InLobbyScene => Game.GameStateMachine != null && Game.State is GameLobby;

        // ================= host =================

        private void Update()
        {
            if (net == null) return;
            if (MatchSync.IsHost) HostUpdate();
            else ClientUpdate();
        }

        private void HostUpdate()
        {
            if (!InLobbyScene || net.Remotes.Count == 0 || Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + SendInterval;
            w.Reset();
            w.U8((byte)Msg.MenuState);
            WriteState(w);
            var bytes = w.ToArray();
            if (lastSent != null && bytes.SequenceEqual(lastSent) && Time.unscaledTime - lastSendTime < ResendInterval) return;
            lastSent = bytes;
            lastSendTime = Time.unscaledTime;
            net.Broadcast(w, true);
        }

        private static void WriteState(NetWriter w)
        {
            var panel = GameLobby.CurrentPanel;
            w.Str(panel != null ? panel.name : "");

            var cs = panel as CharacterSelection;
            var boxes = cs != null ? Traverse.Create(cs).Field("boxes").GetValue<CharacterSelectionBox[]>() : null;
            w.U8((byte)(boxes?.Length ?? 0));
            if (boxes != null)
                foreach (var b in boxes)
                {
                    w.U8((byte)b.State);
                    var ch = b.Characters != null && b.CharacterIndex < b.Characters.Length ? b.Characters[b.CharacterIndex] : null;
                    w.Str(ch != null ? ch.ShortName : "");
                    w.I8((sbyte)(b.SkinSelectionCharacter != null ? b.SkinSelectionCharacter.CurrentSkinIndex : 0));
                    w.I8((sbyte)Mathf.Clamp(b.SelectionConfig != null ? b.SelectionConfig.Difficulty : -1, -1, 127));
                }
            w.Bool(cs != null && cs.ReadyButton != null && cs.ReadyButton.gameObject.activeSelf);

            var ms = Find<ModeSelectPanel>();
            w.Str(CarouselText(ms != null ? ms.ModeCarousel : null));
            w.Str(CarouselText(ms != null ? ms.MatchLengthCarousel : null));

            var ss = Find<StageSelectPanel>();
            string stage = "";
            if (ss != null && ss.Carousel != null && ss.Carousel.Buttons.Count > 0)
            {
                var stages = Traverse.Create(ss).Field("stages").GetValue<Stage[]>();
                int i = ss.Carousel.CurrentItemIndex;
                if (stages != null && i < stages.Length) stage = stages[i].SceneName;
            }
            w.Str(stage);

            var players = GameLobby.Players;
            var teams = Game.GlobalResources.Teams;
            w.U8((byte)(players?.Count ?? 0));
            if (players != null)
                foreach (var p in players)
                {
                    w.Str(p.Character != null ? p.Character.ShortName : "");
                    w.Bool(p.IsBot);
                    w.U8((byte)Mathf.Clamp(p.BotDifficulty, 0, 255));
                    w.I8((sbyte)(p.Team != null ? teams.IndexOf(p.Team) : -1));
                }
        }

        private static string CarouselText(RectTransformCarousel c)
        {
            if (c == null || c.Buttons.Count == 0) return "";
            var b = c.CurrentItem;
            return b != null && b.ButtonText != null ? b.ButtonText.text : "";
        }

        /// <summary>
        /// Host: box 1 is the host's own controller, then one box per online player (by slot), then
        /// any other local pads. Online players are always human boxes.
        /// </summary>
        internal void HostAssignBoxes(CharacterSelection cs)
        {
            var boxes = Traverse.Create(cs).Field("boxes").GetValue<CharacterSelectionBox[]>();
            var devices = new List<InputDevice> { OnlineLocalDevice() };
            devices.AddRange(net.Remotes.Values.OrderBy(r => r.Slot).Select(r => (InputDevice)r.Device));
            foreach (var d in InputManager.Devices)
                if (!(d is NetInputDevice) && !devices.Contains(d)) devices.Add(d);

            for (int j = 0; j < boxes.Length; j++)
            {
                var box = boxes[j];
                var dev = j < devices.Count ? devices[j] : null;
                if (dev is NetInputDevice && box.SelectionConfig != null) box.SelectionConfig.Difficulty = -1;
                box.SetInputDevice(dev);
                if (box.SelectionConfig != null && box.SelectionConfig.IsBot) continue;
                box.SetState(dev != null ? CharacterSelectionBox.SelectionState.Active : CharacterSelectionBox.SelectionState.Inactive);
            }
        }

        internal static InputDevice OnlineLocalDevice()
        {
            var active = InputManager.ActiveDevice;
            if (active != null && !(active is NetInputDevice) && active != InputDevice.Null) return active;
            foreach (var d in InputManager.Devices)
                if (!(d is NetInputDevice)) return d;
            return active;
        }

        // ================= client =================

        private void OnMessage(int peer, Msg msg, ref NetReader r)
        {
            if (msg != Msg.MenuState || !MatchSync.IsClient) return;
            var s = new MenuStateData { Panel = r.Str() };
            int n = r.U8();
            for (int i = 0; i < n; i++)
                s.Boxes.Add(new BoxData { State = r.U8(), Character = r.Str(), Skin = r.I8(), Difficulty = r.I8() });
            s.ReadyButton = r.Bool();
            s.Mode = r.Str();
            s.Length = r.Str();
            s.Stage = r.Str();
            int np = r.U8();
            for (int i = 0; i < np; i++)
                s.Players.Add(new PlayerData { Character = r.Str(), IsBot = r.Bool(), Difficulty = r.U8(), Team = r.I8() });
            pending = s;
        }

        private void ClientUpdate()
        {
            bool connected = net.Role == NetRole.Client && net.IsConnected;
            if (!connected || !InLobbyScene) { SetMirroring(false); pending = null; appliedPanel = null; return; }
            var s = pending;
            if (s == null) return;

            bool hostInFlow = !string.IsNullOrEmpty(s.Panel) && s.Panel != "main_menu";
            SetMirroring(hostInFlow);
            try { Apply(s, hostInFlow); }
            catch (Exception e) { Plugin.Log.LogWarning("MenuSync apply failed: " + e); pending = null; }
        }

        private void Apply(MenuStateData s, bool hostInFlow)
        {
            var current = GameLobby.CurrentPanel;
            string target = hostInFlow ? s.Panel : "main_menu";
            if (current == null) return;

            if (current.name != target && appliedPanel != target)
            {
                var panel = Resources.FindObjectsOfTypeAll<Panel>().FirstOrDefault(p => p.name == target && p.gameObject.scene.IsValid());
                if (panel == null) return;
                // Only follow the host out of the main menu once the client is at the menu itself.
                if (current is MainMenuPanel) UI.NativeOnlineMenu.Instance?.ExitSubmenu();
                if (panel is TeamPanel) GameLobby.Players = BuildPlayers(s.Players);
                Plugin.Trace($"MenuSync: {current.name} -> {target}");
                appliedPanel = target;
                current.Exit();
                panel.PreviousPanel = current;
                panel.Enter();
                return; // apply contents next frame, after Enter has set things up
            }
            if (current.name == target) appliedPanel = null;

            if (current is CharacterSelection cs) ApplyBoxes(cs, s);
            if (current is ModeSelectPanel ms)
            {
                SetCarouselByText(ms.ModeCarousel, s.Mode);
                SetCarouselByText(ms.MatchLengthCarousel, s.Length);
            }
            if (current is StageSelectPanel ss) ApplyStage(ss, s.Stage);
            if (current is TeamPanel tp) ApplyTeams(tp, s.Players);
        }

        private static List<Player> BuildPlayers(List<PlayerData> data)
        {
            var list = new List<Player>();
            for (int i = 0; i < data.Count; i++)
            {
                Character c;
                Game.GlobalResources.Characters.TryGetValue(data[i].Character, out c);
                var p = new Player(c, (PlayerNumber)i, new NetInputDevice("puppet"), null, data[i].IsBot)
                {
                    BotDifficulty = data[i].Difficulty,
                };
                list.Add(p);
            }
            return list;
        }

        private static void ApplyTeams(TeamPanel tp, List<PlayerData> data)
        {
            var boxes = Traverse.Create(tp).Field("boxes").GetValue<List<TeamSelectionBox>>();
            if (boxes == null) return;
            for (int i = 0; i < boxes.Count && i < data.Count; i++)
                if (data[i].Team >= 0 && boxes[i].TeamIndex != data[i].Team) boxes[i].SetTeam(data[i].Team);
        }

        private static void SetCarouselByText(RectTransformCarousel c, string text)
        {
            if (c == null || string.IsNullOrEmpty(text) || CarouselText(c) == text) return;
            var b = c.Buttons.FirstOrDefault(x => x.ButtonText != null && x.ButtonText.text == text);
            if (b != null) c.SetItem(b);
        }

        private static void ApplyStage(StageSelectPanel ss, string scene)
        {
            if (string.IsNullOrEmpty(scene) || ss.Carousel == null) return;
            var stages = Traverse.Create(ss).Field("stages").GetValue<Stage[]>();
            if (stages == null) return;
            int i = Array.FindIndex(stages, x => x.SceneName == scene);
            if (i >= 0 && i < ss.Carousel.Buttons.Count && ss.Carousel.CurrentItemIndex != i)
                ss.Carousel.SetItem(ss.Carousel.Buttons[i]);
        }

        private static void ApplyBoxes(CharacterSelection cs, MenuStateData s)
        {
            var boxes = Traverse.Create(cs).Field("boxes").GetValue<CharacterSelectionBox[]>();
            if (cs.ReadyButton != null && cs.ReadyButton.gameObject.activeSelf != s.ReadyButton)
                cs.ReadyButton.gameObject.SetActive(s.ReadyButton);
            for (int i = 0; i < boxes.Length && i < s.Boxes.Count; i++)
            {
                var box = boxes[i];
                var d = s.Boxes[i];
                var want = (CharacterSelectionBox.SelectionState)d.State;
                bool botChanged = box.SelectionConfig != null && box.SelectionConfig.Difficulty != d.Difficulty;
                if (box.SelectionConfig != null) box.SelectionConfig.Difficulty = d.Difficulty;
                // Bot/human visuals are set on state entry: re-enter the state if bot-ness changed.
                if (botChanged && box.State == want) box.SetState(CharacterSelectionBox.SelectionState.Inactive);

                int idx = Array.FindIndex(box.Characters, c => c.ShortName == d.Character);
                bool indexWrong = idx >= 0 && idx != box.CharacterIndex;
                if (box.State == CharacterSelectionBox.SelectionState.Confirmed && (want != box.State || indexWrong))
                    box.SetState(CharacterSelectionBox.SelectionState.Active);
                if (indexWrong)
                {
                    // Bypass the "skip characters other boxes confirmed" walk; host already resolved it.
                    var ch = box.Characters[idx];
                    bool was = ch.Confirmed;
                    ch.Confirmed = false;
                    box.SetCharacterIndex(idx);
                    ch.Confirmed = was;
                }
                if (box.State != want) box.SetState(want);
                if (want == CharacterSelectionBox.SelectionState.Confirmed && box.SkinSelectionCharacter != null
                    && box.SkinSelectionCharacter.CurrentSkinIndex != d.Skin)
                    box.SkinSelectionCharacter.SetSkinIndex(d.Skin);
            }
        }

        private static void SetMirroring(bool on)
        {
            if (ClientMirroring == on) return;
            ClientMirroring = on;
            var module = EventSystem.current != null ? EventSystem.current.GetComponent<InControlInputModule>() : null;
            if (module != null) module.enabled = !on;
            Plugin.Trace("MenuSync: client mirroring " + (on ? "on" : "off"));
        }

        /// <summary>Client: all boxes can show any character the host's players pick.</summary>
        internal static void ClientPrepareCharacterSelection(CharacterSelection cs)
        {
            var all = Game.GlobalResources.GetCharacters(includeLocked: true);
            foreach (var box in Traverse.Create(cs).Field("boxes").GetValue<CharacterSelectionBox[]>())
            {
                Traverse.Create(box).Field("characters").SetValue(all);
                // Scratch config so mirrored picks never overwrite the client's saved settings.
                box.SelectionConfig = new CharacterSelectionConfig { Difficulty = -1, SkinIndeces = new int[all.Length], CharacterIndex = 0 };
                box.SetInputDevice(null);
                // Start clean (the client's own saved bot setup must not show); host state fills it in.
                box.SetState(CharacterSelectionBox.SelectionState.Inactive);
            }
        }
    }

    // ================= patches =================

    [HarmonyPatch(typeof(CharacterSelection), nameof(CharacterSelection.Enter))]
    internal static class CharacterSelectionEnterPatch
    {
        private static void Postfix(CharacterSelection __instance)
        {
            if (MatchSync.IsHost && NetSession.Instance.Remotes.Count > 0) MenuSync.Instance.HostAssignBoxes(__instance);
            else if (MatchSync.IsClient && MenuSync.ClientMirroring) MenuSync.ClientPrepareCharacterSelection(__instance);
        }
    }

    /// <summary>Client: no local devices join mirrored boxes; no local character-collision shuffling.</summary>
    [HarmonyPatch(typeof(CharacterSelection), "AddDevice")]
    internal static class CsAddDevicePatch
    {
        private static bool Prefix() => !MenuSync.ClientMirroring;
    }

    [HarmonyPatch(typeof(CharacterSelection), "RemoveDevice")]
    internal static class CsRemoveDevicePatch
    {
        private static bool Prefix() => !MenuSync.ClientMirroring;
    }

    [HarmonyPatch(typeof(CharacterSelectionBox), nameof(CharacterSelectionBox.CheckIfCharacterConfirmed))]
    internal static class CheckConfirmedPatch
    {
        private static bool Prefix() => !MenuSync.ClientMirroring;
    }

    /// <summary>Client: menu actions (every panel, box, carousel) go quiet while mirroring.</summary>
    [HarmonyPatch(typeof(PlayerActionSet), "Update")]
    internal static class MenuActionsPatch
    {
        private static void Prefix(PlayerActionSet __instance)
        {
            if (__instance is MenuActions) __instance.Enabled = !MenuSync.ClientMirroring;
        }
    }

    /// <summary>Client never creates lobby states itself; the host's MatchStart does.</summary>
    [HarmonyPatch(typeof(GameLobby), nameof(GameLobby.CreateNextState))]
    internal static class CreateNextStatePatch
    {
        private static bool Prefix() => !MatchSync.IsClient;
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>Online: only modes the netcode supports are offered (Arcane Arena for now).</summary>
    [HarmonyLib.HarmonyPatch(typeof(WandWars.ModeSelectPanel), nameof(WandWars.ModeSelectPanel.Enter))]
    internal static class OnlineModesPatch
    {
        public static readonly string[] Supported = { "arcane arena", "team arena", "merlinball", "hexout", "team hexout", "last hen standing", "hen hunt" };

        private static void Postfix(WandWars.ModeSelectPanel __instance)
        {
            if (!MatchSync.IsHost) return;
            var c = __instance.ModeCarousel;
            foreach (var b in new System.Collections.Generic.List<MenuButton>(c.Buttons))
                if (b.ButtonText != null && System.Array.IndexOf(Supported, b.ButtonText.text.Trim().ToLower()) < 0)
                    c.DisableButton(b);
        }
    }
}
