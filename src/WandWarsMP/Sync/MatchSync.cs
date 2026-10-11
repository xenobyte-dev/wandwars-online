using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Mirrored-match sync. The host runs the real match and streams: match start, per-round spawns,
    /// every actor call (moves each frame, abilities reliably) and periodic state snapshots. Clients
    /// build the same match, strip local control from every entity, and replay what the host sends.
    /// </summary>
    public sealed partial class MatchSync : MonoBehaviour
    {
        public static MatchSync Instance { get; private set; }

        public const byte NoPlayer = 255;
        public const byte AbOne = 1, AbOneRelease = 2, AbTwo = 3, AbThree = 4, AbThreeRelease = 5, AbFour = 6;

        private const int SnapshotEveryFrames = 3;
        private const float SnapDistance = 2.5f;
        private const float PosBlend = 0.25f;

        private NetSession net;
        private uint tick;

        // host
        private readonly Dictionary<byte, Vector2> hostMoves = new Dictionary<byte, Vector2>();

        // client
        private readonly Dictionary<int, RoundSpawnData> roundSpawns = new Dictionary<int, RoundSpawnData>();
        private readonly Dictionary<byte, Vector2> clientMoves = new Dictionary<byte, Vector2>();
        private uint lastFrameTick, lastSnapshotTick;
        private bool haveFrame, haveSnapshot;
        private MatchDesc pendingStart;

        // stats (client)
        public float AvgCorrection { get; private set; }
        public int Snaps { get; private set; }

        public sealed class RoundSpawnData
        {
            public int[] Points;
            public byte BallPlayer;
        }

        public static bool IsHost => NetSession.Instance != null && NetSession.Instance.Role == NetRole.Host;
        public static bool IsClient => NetSession.Instance != null && NetSession.Instance.Role == NetRole.Client;
        public static bool Online => IsHost || IsClient;

        public static void Ensure()
        {
            if (Instance == null) Instance = Plugin.Root.AddComponent<MatchSync>();
        }

        private void Start()
        {
            net = NetSession.Ensure();
            net.Message += OnMessage;
            GameEntity.GameEntityDied += (e, src) =>
            {
                if (Online && Game.Match != null && e != null)
                    Plugin.Trace($"Sync[{(IsHost ? "H" : "C")}]: DIED {e.name} player={(e.SpellOwner?.Player != null ? "P" + ((int)e.SpellOwner.Player.PlayerNumber + 1) : "-")} src={(src != null ? src.name : "null")}");
            };
        }

        // ================= host =================

        public void HostOnMatchCreated(string level, List<Player> players, MatchConfig config)
        {
            int seed = System.Environment.TickCount;
            Random.seed = seed;
            var desc = MatchDesc.FromMatch(level, players, config, OwnerOf, seed);
            var w = net.Begin(Msg.MatchStart);
            desc.Write(w);
            net.Broadcast(w, true);
            hostMoves.Clear();
            ResetCombat();
            Plugin.Trace($"Sync: MatchStart sent ({config.MatchType} on '{level}', {players.Count} players, seed {seed})");
        }

        private byte OwnerOf(InControl.InputDevice device)
        {
            foreach (var rp in net.Remotes.Values)
                if (rp.Device == device) return rp.Slot;
            return 0;
        }

        public void HostSendRoundSpawn(int round, int[] points, Player ballPlayer)
        {
            var w = net.Begin(Msg.RoundSpawn);
            w.U8((byte)round);
            w.U8((byte)points.Length);
            foreach (int p in points) w.U8((byte)p);
            w.U8(ballPlayer == null ? NoPlayer : (byte)ballPlayer.PlayerNumber);
            net.Broadcast(w, true);
        }

        public void HostRecordMove(PlayerController pc, Vector2 dir)
        {
            byte num = PlayerNum(pc);
            if (num != NoPlayer) hostMoves[num] = dir;
        }

        public void HostRecordAbility(PlayerController pc, byte ability)
        {
            byte num = PlayerNum(pc);
            if (num == NoPlayer) return;
            var w = net.Begin(Msg.Ability);
            w.U8(num);
            w.U8(ability);
            Vector2 look = pc.LastMoveDirection;
            w.I8(Quant(look.x));
            w.I8(Quant(look.y));
            net.Broadcast(w, true);
        }

        internal static byte PlayerNum(GameEntity e)
        {
            var player = e.SpellOwner?.Player;
            if (player == null || Game.Match == null || !Game.Match.Players.Contains(player)) return NoPlayer;
            if (player.PrimaryEntity != e) return NoPlayer; // clones etc. not synced yet
            return (byte)player.PlayerNumber;
        }

        internal static sbyte Quant(float v) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(v * 127f), -127, 127);

        private void LateUpdate()
        {
            if (net == null || !IsHost || net.Remotes.Count == 0 || !(Game.State is MatchBase)) return;
            tick++;

            var w = net.Begin(Msg.Frame);
            w.U32(tick);
            w.U8((byte)hostMoves.Count);
            foreach (var kv in hostMoves)
            {
                w.U8(kv.Key);
                w.I8(Quant(kv.Value.x));
                w.I8(Quant(kv.Value.y));
            }
            net.Broadcast(w, false);
            hostMoves.Clear();

            HostTrackBalls();
            if (tick % SnapshotEveryFrames == 0) SendSnapshot();
        }

        private void SendSnapshot()
        {
            var match = Game.Match;
            var w = net.Begin(Msg.Snapshot);
            w.U32(tick);
            var list = match.Players;
            w.U8((byte)list.Count);
            foreach (var p in list)
            {
                w.U8((byte)p.PlayerNumber);
                var e = p.PrimaryEntity;
                var rb = e != null ? e.Rigidbody2D : null;
                bool has = rb != null && e.gameObject.activeInHierarchy;
                w.Bool(has);
                if (!has) continue;
                w.Vec2(rb.position);
                w.Vec2(rb.velocity);
            }
            WriteBallSnapshot(w);
            net.Broadcast(w, false);
        }

        // ================= client =================

        private void OnMessage(int peer, Msg msg, ref NetReader r)
        {
            if (!IsClient) return;
            switch (msg)
            {
                case Msg.MatchStart: OnMatchStart(MatchDesc.Read(ref r)); break;
                case Msg.RoundSpawn: OnRoundSpawn(ref r); break;
                case Msg.Frame: OnFrame(ref r); break;
                case Msg.Ability: OnAbility(ref r); break;
                case Msg.Snapshot: OnSnapshot(ref r); break;
                default: OnCombatMessage(msg, ref r); break;
            }
        }

        private void OnMatchStart(MatchDesc desc)
        {
            Plugin.Trace($"Sync: MatchStart received ({desc.Type} on '{desc.Level}', {desc.Players.Count} players)");
            pendingStart = desc;
            ClientLeaveScoreScreen();
            roundSpawns.Clear();
            clientMoves.Clear();
            ResetCombat();
            haveFrame = haveSnapshot = false;
            Snaps = 0;
            AvgCorrection = 0;
        }

        private void Update()
        {
            if (!IsClient) return;

            if (pendingStart != null && Game.GameStateMachine != null && Game.State is GameLobby)
            {
                var desc = pendingStart;
                pendingStart = null;
                Random.seed = desc.Seed;
                var players = desc.BuildPlayers(_ => new NetInputDevice("puppet"));
                var match = GameLobby.CreateMatch(desc.Level, players, desc.ToConfig());
                Game.GameStateMachine.Start(match);
            }

            var m = Game.Match;
            if (m == null) return;
            using (Replay.Begin())
            {
                foreach (var kv in clientMoves)
                {
                    if (kv.Value == Vector2.zero) continue;
                    var pc = EntityOf(kv.Key);
                    if (pc != null) pc.Move(kv.Value);
                }
            }
        }

        internal static PlayerController EntityOf(byte num)
        {
            var m = Game.Match;
            if (m == null) return null;
            foreach (var p in m.Players)
                if ((byte)p.PlayerNumber == num) return p.PrimaryEntity as PlayerController;
            return null;
        }

        private void OnRoundSpawn(ref NetReader r)
        {
            int round = r.U8();
            int n = r.U8();
            var pts = new int[n];
            for (int i = 0; i < n; i++) pts[i] = r.U8();
            roundSpawns[round] = new RoundSpawnData { Points = pts, BallPlayer = r.U8() };
        }

        public RoundSpawnData TakeRoundSpawn(int round)
        {
            if (!roundSpawns.TryGetValue(round, out var d)) return null;
            roundSpawns.Remove(round);
            return d;
        }

        private void OnFrame(ref NetReader r)
        {
            uint t = r.U32();
            if (haveFrame && (int)(t - lastFrameTick) <= 0) return;
            haveFrame = true;
            lastFrameTick = t;
            clientMoves.Clear();
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                byte num = r.U8();
                clientMoves[num] = new Vector2(r.I8() / 127f, r.I8() / 127f);
            }
        }

        private void OnAbility(ref NetReader r)
        {
            byte num = r.U8();
            byte ability = r.U8();
            var look = new Vector2(r.I8() / 127f, r.I8() / 127f);
            var pc = EntityOf(num);
            if (pc == null) return;
            using (Replay.Begin())
            {
                if (look != Vector2.zero) Traverse.Create(pc).Field("lastMoveDirection").SetValue(look.normalized);
                switch (ability)
                {
                    case AbOne: pc.AbilityOne(); break;
                    case AbOneRelease: pc.AbilityOneRelease(); break;
                    case AbTwo: pc.AbilityTwo(); break;
                    case AbThree: pc.AbilityThree(); break;
                    case AbThreeRelease: pc.AbilityThreeRelease(); break;
                    case AbFour: pc.AbilityFour(); break;
                }
            }
        }

        private void OnSnapshot(ref NetReader r)
        {
            uint t = r.U32();
            if (haveSnapshot && (int)(t - lastSnapshotTick) <= 0) return;
            haveSnapshot = true;
            lastSnapshotTick = t;
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                byte num = r.U8();
                if (!r.Bool()) continue;
                Vector2 pos = r.Vec2(), vel = r.Vec2();
                var pc = EntityOf(num);
                var rb = pc != null ? pc.Rigidbody2D : null;
                if (rb == null || !pc.gameObject.activeInHierarchy) continue;
                Vector2 err = pos - rb.position;
                float mag = err.magnitude;
                AvgCorrection = AvgCorrection * 0.95f + mag * 0.05f;
                if (mag > SnapDistance) { rb.position = pos; Snaps++; }
                else rb.position += err * PosBlend;
                rb.velocity = vel;
            }
            ReadBallSnapshot(ref r);
        }
    }
}
