using System.Collections.Generic;
using System.Linq;
using InControl;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Pre-match lobby (Arcane Arena / Deathmatch). Host owns the state: settings + one slot per
    /// connected player with their character pick and ready flag. Clients send picks; host broadcasts
    /// the full state on every change and starts the match through GameLobby.CreateMatch.
    /// </summary>
    public sealed class OnlineLobby : MonoBehaviour
    {
        public static OnlineLobby Instance { get; private set; }

        public const byte RandomPick = 255;
        public const int MaxPlayers = 4;

        public sealed class Slot
        {
            public byte Id;          // net slot: 0 = host
            public string Name;
            public byte Character = RandomPick; // index into Characters
            public bool Ready;
        }

        // ---- shared state (host authoritative, mirrored on clients) ----
        public readonly List<Slot> Slots = new List<Slot>();
        public List<string> Characters = new List<string>();     // Character.ShortName
        public List<string> CharacterNames = new List<string>(); // display names
        public List<string> Stages = new List<string>();         // scene names, [0] = random
        public List<string> StageNames = new List<string>();
        public int StageIndex;
        public int ScoreLimit = 4;
        public int Bots;
        public int BotDifficulty = 50;
        public bool HasState { get; private set; }

        private NetSession net;
        private bool dirty;

        public static void Ensure()
        {
            if (Instance == null) Instance = Plugin.Root.AddComponent<OnlineLobby>();
        }

        private void Start()
        {
            net = NetSession.Ensure();
            net.Message += OnMessage;
            net.RemoteJoined += _ => { if (MatchSync.IsHost) { EnsureHostState(); dirty = true; } };
            net.RemoteLeft += rp => { Slots.RemoveAll(s => s.Id == rp.Slot); dirty = true; };
        }

        public Slot LocalSlot
        {
            get
            {
                byte id = MatchSync.IsHost ? (byte)0 : (byte)Mathf.Max(0, net.LocalSlot);
                return Slots.Find(s => s.Id == id);
            }
        }

        public bool InMenu => Game.GameStateMachine != null && Game.State is GameLobby;

        // ================= host =================

        private void EnsureHostState()
        {
            if (HasState) return;
            HasState = true;
            var chars = Game.GlobalResources.GetCharacters();
            Characters = chars.Select(c => c.ShortName).ToList();
            CharacterNames = chars.Select(c => c.ShortNameCapitalized).ToList();
            Stages = new List<string> { "" };
            StageNames = new List<string> { "Random" };
            foreach (var st in Game.GlobalResources.Stages.OrderBy(x => x.Order))
            {
                if (st.Hidden || st.SceneName.ToLower() == "random") continue;
                Stages.Add(st.SceneName);
                StageNames.Add(string.IsNullOrEmpty(st.DisplayName) ? st.SceneName : st.DisplayName);
            }
        }

        private void Update()
        {
            if (net == null) return;
            if (net.Role == NetRole.None) { if (HasState) { HasState = false; Slots.Clear(); } return; }
            if (!MatchSync.IsHost) return;

            EnsureHostState();
            if (Slots.Find(s => s.Id == 0) == null) { Slots.Insert(0, new Slot { Id = 0, Name = net.LocalName }); dirty = true; }
            foreach (var rp in net.Remotes.Values)
                if (Slots.Find(s => s.Id == rp.Slot) == null) { Slots.Add(new Slot { Id = rp.Slot, Name = rp.Name }); dirty = true; }
            Slots.Sort((a, b) => a.Id.CompareTo(b.Id));
            Bots = Mathf.Clamp(Bots, 0, MaxPlayers - Slots.Count);
            if (dirty) { dirty = false; BroadcastState(); }
        }

        public void MarkDirty() => dirty = true;

        private void BroadcastState()
        {
            var w = net.Begin(Msg.LobbyState);
            w.U8((byte)Characters.Count);
            for (int i = 0; i < Characters.Count; i++) { w.Str(Characters[i]); w.Str(CharacterNames[i]); }
            w.U8((byte)Stages.Count);
            for (int i = 0; i < Stages.Count; i++) { w.Str(Stages[i]); w.Str(StageNames[i]); }
            w.U8((byte)StageIndex);
            w.U8((byte)ScoreLimit);
            w.U8((byte)Bots);
            w.U8((byte)BotDifficulty);
            w.U8((byte)Slots.Count);
            foreach (var s in Slots) { w.U8(s.Id); w.Str(s.Name); w.U8(s.Character); w.Bool(s.Ready); }
            net.Broadcast(w, true);
        }

        public bool CanStart => MatchSync.IsHost && InMenu && Slots.Count + Bots >= 2 && Slots.All(s => s.Id == 0 || s.Ready);

        /// <summary>Host: build players from the lobby and start the match for everyone.</summary>
        public void StartMatch()
        {
            if (!CanStart) return;
            var used = new HashSet<string>();
            var all = Game.GlobalResources.GetCharacters().ToList();
            Character Pick(byte idx)
            {
                if (idx != RandomPick && idx < Characters.Count && Game.GlobalResources.Characters.TryGetValue(Characters[idx], out var c))
                {
                    used.Add(c.ShortName);
                    return c;
                }
                var free = all.Where(x => !used.Contains(x.ShortName)).ToList();
                var r = (free.Count > 0 ? free : all)[Random.Range(0, (free.Count > 0 ? free : all).Count)];
                used.Add(r.ShortName);
                return r;
            }

            // Explicit picks first so random picks avoid duplicating them.
            var chosen = new Dictionary<Slot, Character>();
            foreach (var s in Slots.Where(x => x.Character != RandomPick)) chosen[s] = Pick(s.Character);
            foreach (var s in Slots.Where(x => x.Character == RandomPick)) chosen[s] = Pick(RandomPick);

            var players = new List<Player>();
            int num = 0;
            foreach (var s in Slots)
            {
                InputDevice device = s.Id == 0 ? LocalDevice()
                    : net.Remotes.Values.First(r => r.Slot == s.Id).Device;
                players.Add(new Player(chosen[s], (PlayerNumber)num++, device));
            }
            for (int i = 0; i < Bots; i++)
                players.Add(new Player(Pick(RandomPick), (PlayerNumber)num++, null, null, isBot: true) { BotDifficulty = BotDifficulty });

            string stage = Stages[StageIndex];
            if (string.IsNullOrEmpty(stage)) stage = WandWars.Stages.GetRandomStage();
            var config = new MatchConfig { MatchType = MatchType.Deathmatch, ScoreLimit = ScoreLimit, BotDifficulty = BotDifficulty };
            foreach (var s in Slots) s.Ready = false;
            dirty = true;
            Plugin.Trace($"Lobby: starting Arcane Arena on {stage}: {string.Join(", ", players.Select(p => p.Character.ShortName).ToArray())}");
            Game.GameStateMachine.Start(GameLobby.CreateMatch(stage, players, config));
        }

        /// <summary>The host's own controller: last used device that isn't a remote player's.</summary>
        private static InputDevice LocalDevice()
        {
            var active = InputManager.ActiveDevice;
            if (active != null && !(active is NetInputDevice) && active != InputDevice.Null) return active;
            foreach (var d in InputManager.Devices)
                if (!(d is NetInputDevice)) return d;
            return active;
        }

        // ================= client =================

        public void SendPick(byte character, bool ready)
        {
            if (MatchSync.IsHost)
            {
                var me = LocalSlot;
                if (me != null) { me.Character = character; me.Ready = ready; dirty = true; }
                return;
            }
            var w = net.Begin(Msg.LobbyPick);
            w.U8(character);
            w.Bool(ready);
            net.SendTo(0, w, true);
        }

        private void OnMessage(int peer, Msg msg, ref NetReader r)
        {
            if (msg == Msg.LobbyPick && MatchSync.IsHost)
            {
                byte ch = r.U8();
                bool ready = r.Bool();
                if (!net.Remotes.TryGetValue(peer, out var rp)) return;
                var s = Slots.Find(x => x.Id == rp.Slot);
                if (s == null) return;
                s.Character = ch < Characters.Count ? ch : RandomPick;
                s.Ready = ready;
                dirty = true;
            }
            else if (msg == Msg.LobbyState && MatchSync.IsClient)
            {
                Characters = new List<string>(); CharacterNames = new List<string>();
                int n = r.U8();
                for (int i = 0; i < n; i++) { Characters.Add(r.Str()); CharacterNames.Add(r.Str()); }
                Stages = new List<string>(); StageNames = new List<string>();
                n = r.U8();
                for (int i = 0; i < n; i++) { Stages.Add(r.Str()); StageNames.Add(r.Str()); }
                StageIndex = r.U8();
                ScoreLimit = r.U8();
                Bots = r.U8();
                BotDifficulty = r.U8();
                Slots.Clear();
                n = r.U8();
                for (int i = 0; i < n; i++) Slots.Add(new Slot { Id = r.U8(), Name = r.Str(), Character = r.U8(), Ready = r.Bool() });
                HasState = true;
            }
        }
    }
}
