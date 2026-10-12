using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Staple;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Host-authoritative outcomes: ball lifecycle/possession, damage and deaths, hexes, round winners.
    /// Clients block the corresponding local logic (see CombatPatches) and apply these messages instead.
    /// </summary>
    public sealed partial class MatchSync
    {
        private const byte SrcNone = 0, SrcBall = 1, SrcPlayer = 2;

        private readonly Dictionary<Ball, ushort> ballIds = new Dictionary<Ball, ushort>();
        private readonly Dictionary<ushort, Ball> ballsById = new Dictionary<ushort, Ball>();
        private ushort nextBallId = 1;
        private readonly NetWriter spawnWriter = new NetWriter();
        private readonly List<Ball> scratchBalls = new List<Ball>();

        // client: round winners received from host
        private readonly Dictionary<int, RoundWinnerData> roundWinners = new Dictionary<int, RoundWinnerData>();
        private readonly HashSet<int> roundsOver = new HashSet<int>();      // client: host said round ended
        private readonly HashSet<int> roundsAwaitingScore = new HashSet<int>(); // client: used local winner
        private int hostRoundOverSent = -1;

        public const byte WinNone = 0, WinPlayer = 1, WinTeam = 2;

        public sealed class RoundWinnerData
        {
            public byte WinnerKind;
            public byte Winner;
            public Dictionary<byte, int> Scores = new Dictionary<byte, int>();
            public Dictionary<byte, int> TeamScores = new Dictionary<byte, int>();
        }

        /// <summary>True while the client is creating a ball the host told it about.</summary>
        internal static bool SpawningNetBall;
        /// <summary>Host: nesting depth of GameEntity.TakeDamage (deaths inside it ride on the Damage message).</summary>
        internal static int DamageDepth;

        private void ResetCombat()
        {
            ballIds.Clear();
            ballsById.Clear();
            roundWinners.Clear();
            roundsOver.Clear();
            fastForwardRound = -1;
            roundsAwaitingScore.Clear();
            hostRoundOverSent = -1;
            nextBallId = 1;
            DamageDepth = 0;
            deferred.Clear();
        }

        // ================= balls (host) =================

        internal ushort HostBallId(Ball ball)
        {
            if (ball == null) return 0;
            if (ballIds.TryGetValue(ball, out var id)) return id;
            id = nextBallId++;
            if (nextBallId == 0) nextBallId = 1;
            ballIds[ball] = id;
            ballsById[id] = ball;
            var rb = ball.Rigidbody2D;
            // Own writer: this can run while another message is being built in the shared one.
            var w = spawnWriter;
            w.Reset();
            w.U8((byte)Msg.BallSpawn);
            w.U16(id);
            w.Vec2(rb.position);
            w.Vec2(rb.velocity);
            w.U8(OwnerNum(ball.Owner));
            w.U8((byte)ball.NormalizedSpeed);
            w.F32(ball.Size);
            net.Broadcast(w, true);
            return id;
        }

        private static byte OwnerNum(SpellOwner owner)
        {
            var e = owner?.Entity;
            return e == null ? NoPlayer : PlayerNum(e);
        }

        private void HostTrackBalls()
        {
            foreach (var b in Ball.Balls) HostBallId(b);
            scratchBalls.Clear();
            foreach (var kv in ballIds)
                if (kv.Key == null || !kv.Key.isActiveAndEnabled) scratchBalls.Add(kv.Key);
            foreach (var b in scratchBalls)
            {
                ushort id = ballIds[b];
                ballIds.Remove(b);
                ballsById.Remove(id);
                var w = net.Begin(Msg.BallDespawn);
                w.U16(id);
                net.Broadcast(w, true);
            }
        }

        private void WriteBallSnapshot(NetWriter w)
        {
            w.U8((byte)ballIds.Count);
            foreach (var kv in ballIds)
            {
                var b = kv.Key;
                var rb = b.Rigidbody2D;
                w.U16(kv.Value);
                w.Vec2(rb.position);
                w.Vec2(rb.velocity);
                w.U8((byte)b.NormalizedSpeed);
                w.F32(b.Size);
                w.U8((byte)b.State);
                w.U8(OwnerNum(b.Owner));
            }
        }

        // ================= balls (client) =================

        private void ReadBallSnapshot(ref NetReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                ushort id = r.U16();
                Vector2 pos = r.Vec2(), vel = r.Vec2();
                int speed = r.U8();
                float size = r.F32();
                var state = (Ball.BallState)r.U8();
                byte owner = r.U8();
                if (!ballsById.TryGetValue(id, out var b) || b == null) continue;
                var rb = b.Rigidbody2D;
                bool held = b.CurrentCatchSpell != null;
                if (!held)
                {
                    Vector2 err = pos - rb.position;
                    if (err.magnitude > SnapDistance) rb.position = pos; else rb.position += err * PosBlend;
                    rb.velocity = vel;
                }
                b.NormalizedSpeed = speed;
                if (Mathf.Abs(b.Size - size) > 0.01f) b.Size = size;
                if (b.State != state) b.SetState(state);
                if (OwnerNum(b.Owner) != owner && !held)
                {
                    var pc = owner == NoPlayer ? null : ActorOf(owner);
                    b.Owner = pc != null ? pc.SpellOwner : null;
                }
            }
        }

        private void OnBallSpawn(ref NetReader r)
        {
            ushort id = r.U16();
            Vector2 pos = r.Vec2(), vel = r.Vec2();
            byte owner = r.U8();
            int speed = r.U8();
            float size = r.F32();
            if (ballsById.ContainsKey(id)) return;
            Ball b;
            SpawningNetBall = true;
            try { b = Staple.Scene.InstantiateInScene<Ball>(Game.GlobalResources.BallPrefab, pos, Quaternion.identity); }
            finally { SpawningNetBall = false; }
            ballsById[id] = b;
            ballIds[b] = id;
            var pc = owner == NoPlayer ? null : ActorOf(owner);
            if (pc != null) b.Owner = pc.SpellOwner;
            b.NormalizedSpeed = speed;
            b.Size = size;
            b.Rigidbody2D.velocity = vel;
        }

        private void OnBallDespawn(ref NetReader r)
        {
            ushort id = r.U16();
            if (!ballsById.TryGetValue(id, out var b)) return;
            ballsById.Remove(id);
            if (b == null) return;
            ballIds.Remove(b);
            if (b.TrailParticles != null) Object.Destroy(b.TrailParticles.gameObject);
            Object.Destroy(b.gameObject);
        }

        private Ball BallById(ushort id) => ballsById.TryGetValue(id, out var b) ? b : null;

        // ================= catch / launch / release =================

        internal void HostOnCatch(CatchSpell cs, CatchSpell.BallInfo info, Vector2 ballOffset)
        {
            byte num = OwnerNum(cs.Owner);
            if (num == NoPlayer) return;
            HostBallId(info.Ball);
            var w = net.Begin(Msg.Catch);
            w.U8(num);
            w.U16(HostBallId(info.Ball));
            w.Vec2(ballOffset);
            w.Vec2(info.PreCatchVelocity);
            net.Broadcast(w, true);
        }

        internal void HostOnLaunch(CatchSpell cs, CatchSpell.BallInfo info)
        {
            byte num = OwnerNum(cs.Owner);
            if (num == NoPlayer || info.Ball == null) return;
            HostBallId(info.Ball);
            var w = net.Begin(Msg.Launch);
            w.U8(num);
            w.U16(HostBallId(info.Ball));
            w.Vec2(cs.Direction);
            w.U8((byte)info.Ball.NormalizedSpeed);
            net.Broadcast(w, true);
        }

        internal void HostOnRelease(CatchSpell cs, List<Ball> released)
        {
            byte num = OwnerNum(cs.Owner);
            if (num == NoPlayer) return;
            foreach (var b in released) HostBallId(b);
            var w = net.Begin(Msg.Release);
            w.U8(num);
            w.Vec2(cs.Direction);
            w.U8((byte)released.Count);
            foreach (var b in released)
            {
                w.U16(HostBallId(b));
                w.U8((byte)b.NormalizedSpeed);
            }
            net.Broadcast(w, true);
        }

        private void OnCatch(ref NetReader r)
        {
            var pc = EntityOf(r.U8());
            var ball = BallById(r.U16());
            Vector2 offset = r.Vec2(), preVel = r.Vec2();
            if (pc == null || ball == null || pc.CatchSpell == null) return;
            if (!ball.gameObject.activeInHierarchy || !pc.gameObject.activeInHierarchy)
            {
                Plugin.Trace("Sync[C]: catch skipped (ball or catcher inactive locally)");
                return;
            }
            var cs = pc.CatchSpell;
            // Recreate the host's pre-catch geometry so HandleCatch's own checks pass.
            ball.UnlockOwner();
            ball.LastCatchTimeStamp = -1f;
            Vector2 p = (Vector2)cs.transform.position + offset;
            ball.transform.position = p;
            ball.Rigidbody2D.position = p;
            ball.Rigidbody2D.velocity = preVel;
            int before = cs.HeldBalls.Count;
            BallReplay.Active = true;
            try { Traverse.Create(cs).Method("HandleCatch", new CatchSpell.BallInfo(ball)).GetValue(); }
            finally { BallReplay.Active = false; }
            if (cs.HeldBalls.Count == before) Plugin.Log.LogWarning("Sync: catch replay rejected by local checks");
        }

        private void OnLaunch(ref NetReader r)
        {
            var pc = EntityOf(r.U8());
            var ball = BallById(r.U16());
            Vector2 dir = r.Vec2();
            int speed = r.U8();
            if (pc == null || ball == null || pc.CatchSpell == null || !ball.gameObject.activeInHierarchy) return;
            var cs = pc.CatchSpell;
            var info = cs.HeldBalls.Find(x => x.Ball == ball);
            cs.Direction = dir;
            if (info != null)
            {
                BallReplay.Active = true;
                try { Traverse.Create(cs).Method("Launch", info).GetValue(); }
                finally { BallReplay.Active = false; }
                cs.HeldBalls.Remove(info);
            }
            else
            {
                ball.UnlockOwner();
                ball.CurrentCatchSpell = null;
                ball.SetState(Ball.BallState.Normal);
                ball.SetDirection(dir);
            }
            ball.NormalizedSpeed = speed;
        }

        private void OnRelease(ref NetReader r)
        {
            var pc = EntityOf(r.U8());
            Vector2 dir = r.Vec2();
            int n = r.U8();
            var speeds = new Dictionary<Ball, int>();
            for (int i = 0; i < n; i++)
            {
                var b = BallById(r.U16());
                int s = r.U8();
                if (b != null) speeds[b] = s;
            }
            if (pc == null || pc.CatchSpell == null) return;
            var cs = pc.CatchSpell;
            cs.Direction = dir;
            BallReplay.Active = true;
            try { cs.ReleaseAllBalls(); }
            finally { BallReplay.Active = false; }
            foreach (var kv in speeds)
            {
                kv.Key.NormalizedSpeed = kv.Value;
                if (kv.Key.CurrentCatchSpell == cs) // wasn't in the local held list: release manually
                {
                    kv.Key.UnlockOwner();
                    kv.Key.CurrentCatchSpell = null;
                    kv.Key.SetState(Ball.BallState.Normal);
                    kv.Key.SetDirection(dir);
                }
            }
        }

        // ================= damage / death =================

        private void WriteSource(NetWriter w, GameObject src)
        {
            Ball ball = src != null ? src.GetComponent<Ball>() : null;
            if (ball != null) { w.U8(SrcBall); w.U16(HostBallId(ball)); return; }
            ISpell spell = src != null ? src.GetComponent(typeof(ISpell)) as ISpell : null;
            byte owner = spell != null ? OwnerNum(spell.Owner) : NoPlayer;
            if (owner != NoPlayer) { w.U8(SrcPlayer); w.U16(owner); return; }
            w.U8(SrcNone); w.U16(0);
        }

        /// <summary>Client: a GameObject that gives the game the same kill attribution as on the host.</summary>
        private GameObject ReadSource(ref NetReader r, GameEntity target)
        {
            byte kind = r.U8();
            ushort id = r.U16();
            if (kind == SrcBall)
            {
                var b = BallById(id);
                if (b != null) return b.gameObject;
                Plugin.Trace($"Sync[C]: kill source ball {id} missing (known: {string.Join(",", ballsById.Select(kv => kv.Key + (kv.Value == null ? "x" : "")).ToArray())})");
            }
            if (kind == SrcPlayer)
            {
                // Spell kill: any live spell cast by that player gives the game the same attribution.
                foreach (var sp in Object.FindObjectsOfType<Spell>())
                {
                    var owner = sp.Owner?.Entity;
                    if (owner != null && PlayerNum(owner) == id) return sp.gameObject;
                }
            }
            return target.gameObject; // no credit (suicide-like), same as the game for unknown sources
        }

        internal void HostOnDamage(GameEntity target, byte num, int amount, GameObject src, int preHealth, bool died)
        {
            if (src != null && src.GetComponent<Ball>() != null) HostBallId(src.GetComponent<Ball>());
            Plugin.Trace($"Sync[H]: damage P{num + 1} amt={amount} src={(src != null ? src.name : "null")} pre={preHealth} died={died}");
            if (num == NoPlayer) return;
            var w = net.Begin(Msg.Damage);
            w.U8(num);
            w.U8((byte)Mathf.Clamp(amount, 0, 255));
            WriteSource(w, src);
            w.U8((byte)Mathf.Clamp(preHealth, 0, 255));
            w.Bool(died);
            w.I16((short)Mathf.Clamp(target.Health, -30000, 30000));
            net.Broadcast(w, true);
        }

        private static string killSource(GameObject src) => src != null ? src.name : "null";

        internal void HostOnDeath(GameEntity target, GameObject src)
        {
            byte num = PlayerNum(target);
            if (src != null && src.GetComponent<Ball>() != null) HostBallId(src.GetComponent<Ball>());
            Plugin.Trace($"Sync[H]: death P{num + 1} src={(killSource(src))}");
            if (num == NoPlayer) return;
            var w = net.Begin(Msg.Death);
            w.U8(num);
            WriteSource(w, src);
            net.Broadcast(w, true);
        }

        private void OnDamage(ref NetReader r)
        {
            byte targetNum = r.U8();
            var pc = ActorOf(targetNum);
            int amount = r.U8();
            if (pc == null)
            {
                var pl = PlayerOf(targetNum);
                Plugin.Trace($"Sync[C]: damage for P{targetNum + 1} has no local entity (entities={pl?.Entities.Count ?? -1}, primary={(pl?.PrimaryEntity != null ? pl.PrimaryEntity.name : "null")})");
                r.U8(); r.U16(); return;
            }
            var src = ReadSource(ref r, pc);
            int preHealth = r.U8();
            bool died = r.Bool();
            int postHealth = r.I16();
            using (Replay.Begin())
            {
                pc.SetImmunityTime(0f);
                // Survived on the host (e.g. shield absorbed it): replay effects without a lethal outcome.
                pc.Health = died ? preHealth : preHealth + amount + 1;
                pc.TakeDamage(amount, src);
                if (!died && pc != null) pc.Health = postHealth;
                bool stillAlive = pc != null && pc.Player != null && pc.Player.Entities.Contains(pc);
                Plugin.Trace($"Sync[C]: damage P{(int)pc.Player.PlayerNumber + 1} amt={amount} src={src.name} died={died} aliveAfterReplay={stillAlive}");
                if (died && stillAlive) pc.Die(src);
            }
        }

        private void OnDeath(ref NetReader r)
        {
            var pc = ActorOf(r.U8());
            if (pc == null) { r.U8(); r.U16(); return; }
            var src = ReadSource(ref r, pc);
            Plugin.Trace($"Sync[C]: death P{(int)pc.Player.PlayerNumber + 1} src={src.name}");
            using (Replay.Begin()) pc.Die(src);
        }

        // ================= arcane shots =================

        internal void HostOnShotDead(ArcaneShot shot)
        {
            byte owner = OwnerNum(shot.Owner);
            if (owner == NoPlayer) return;
            var w = net.Begin(Msg.ShotDead);
            w.U8(owner);
            w.Vec2(shot.transform.position);
            net.Broadcast(w, true);
        }

        private void OnShotDead(ref NetReader r)
        {
            byte owner = r.U8();
            Vector2 pos = r.Vec2();
            ArcaneShot best = null;
            float bestD = 6f; // only shots reasonably near where the host's died
            foreach (var shot in Object.FindObjectsOfType<ArcaneShot>())
            {
                if (shot.State == ArcaneShot.ArcaneShotState.Dead || OwnerNum(shot.Owner) != owner) continue;
                float d = Vector2.Distance(shot.transform.position, pos);
                if (d < bestD) { bestD = d; best = shot; }
            }
            if (best == null) return;
            best.transform.position = pos;
            using (Replay.Begin()) best.SetState(ArcaneShot.ArcaneShotState.Dead);
        }

        // ================= deferred events =================

        private struct Deferred
        {
            public Msg Msg;
            public byte[] Data;
            public float Until;
        }

        private readonly List<Deferred> deferred = new List<Deferred>();
        private const float DeferSeconds = 3f;

        private void Defer(Msg msg, byte[] data)
        {
            deferred.Add(new Deferred { Msg = msg, Data = data, Until = Time.realtimeSinceStartup + DeferSeconds });
        }

        private void Dispatch(Msg msg, ref NetReader r)
        {
            switch (msg)
            {
                case Msg.Damage: OnDamage(ref r); break;
                case Msg.Death: OnDeath(ref r); break;
                case Msg.Hex: OnHex(ref r); break;
            }
        }

        /// <summary>Client, every frame: replay deferred events whose target now exists (in order).</summary>
        private void PumpDeferred()
        {
            if (deferred.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < deferred.Count; i++)
            {
                var d = deferred[i];
                var r = new NetReader(d.Data, 0, d.Data.Length);
                if (ActorOf(r.PeekU8()) != null)
                {
                    try { Dispatch(d.Msg, ref r); }
                    catch (System.Exception e) { Plugin.Log.LogWarning("Sync: deferred event failed: " + e); }
                    deferred.RemoveAt(i--);
                }
                else if (now > d.Until)
                {
                    Plugin.Trace($"Sync[C]: dropped {d.Msg} for P{d.Data[0] + 1} (never spawned locally)");
                    deferred.RemoveAt(i--);
                }
            }
        }

        // ================= hex =================

        internal void HostOnHex(GameEntity target, float duration)
        {
            byte num = PlayerNum(target);
            if (num == NoPlayer) return;
            var w = net.Begin(Msg.Hex);
            w.U8(num);
            w.F32(duration);
            net.Broadcast(w, true);
        }

        private void OnHex(ref NetReader r)
        {
            var pc = ActorOf(r.U8());
            float duration = r.F32();
            if (pc == null || !pc.gameObject.activeInHierarchy) return;
            using (Replay.Begin())
            {
                if (pc.AnyRoutinesRunning("hex")) pc.StopAllRoutines("hex");
                pc.StartRoutine("hex", HexSpell.TransformIntoAvatar(pc, pc.HexPrefab, duration));
            }
            if (pc.Player != null) pc.Player.Hexed++;
        }

        // ================= rounds =================

        internal void HostOnRoundWinner(MatchBase match, IScoreProvider winner)
        {
            var w = net.Begin(Msg.RoundWinner);
            w.U8((byte)match.RoundNumber);
            Player single = winner is Player wp0 ? wp0 : null;
            if (single == null && !(winner is Team) && winner != null && winner.PlayerList != null)
            {
                // Mode-specific providers (e.g. Hen Hunt's feather counter) wrapping one player.
                var list = new List<Player>(winner.PlayerList);
                if (list.Count == 1) single = list[0];
            }
            if (single != null) { w.U8(WinPlayer); w.U8((byte)single.PlayerNumber); }
            else if (winner is Team wt) { w.U8(WinTeam); w.U8((byte)wt.Index); }
            else { w.U8(WinNone); w.U8(0); }
            w.U8((byte)match.Players.Count);
            foreach (var p in match.Players)
            {
                w.U8((byte)p.PlayerNumber);
                w.I32(p.Score != null ? p.Score.Value : 0);
            }
            var teams = match.Teams;
            w.U8((byte)teams.Count);
            for (int i = 0; i < teams.Count; i++)
            {
                w.U8((byte)i);
                w.I32(teams[i].Score != null ? teams[i].Score.Value : 0);
            }
            net.Broadcast(w, true);
            string who = winner is Player p2 ? "P" + ((int)p2.PlayerNumber + 1) : winner is Team t2 ? "team " + t2.Index : "none";
            Plugin.Trace($"Sync[H]: round {match.RoundNumber} winner {who}");
        }

        internal static IScoreProvider ResolveWinner(RoundWinnerData d)
        {
            var m = Game.Match;
            if (m == null) return null;
            if (d.WinnerKind == WinPlayer) return m.Players.Find(p => (byte)p.PlayerNumber == d.Winner);
            if (d.WinnerKind == WinTeam && d.Winner < m.Teams.Count) return m.Teams[d.Winner];
            return null;
        }

        internal void HostOnRoundOver(MatchBase match)
        {
            if (hostRoundOverSent == match.RoundNumber) return;
            hostRoundOverSent = match.RoundNumber;
            var w = net.Begin(Msg.RoundOver);
            w.U8((byte)match.RoundNumber);
            net.Broadcast(w, true);
            Plugin.Trace($"Sync[H]: round {match.RoundNumber} over");
        }

        internal bool ClientRoundOver(int round) => roundsOver.Contains(round);

        private int fastForwardRound = -1;

        internal bool ClientFastForward(int round) => fastForwardRound == round;

        internal void HostOnFastForward(MatchBase match)
        {
            hostRoundOverSent = match.RoundNumber; // FF replaces the RoundOver signal for this round
            var w = net.Begin(Msg.FastForward);
            w.U8((byte)match.RoundNumber);
            net.Broadcast(w, true);
            Plugin.Trace($"Sync[H]: round {match.RoundNumber} fast-forwarded");
        }
        internal bool HasRoundWinner(int round) => roundWinners.ContainsKey(round);

        internal void HostAbortMatch()
        {
            net.Broadcast(net.Begin(Msg.MatchAbort), true);
            Plugin.Trace("Sync[H]: match ended early by host");
        }

        internal void HostOnPostMatch()
        {
            net.Broadcast(net.Begin(Msg.PostMatch), true);
        }

        /// <summary>Client: leave the score screen if it's up (host moved on).</summary>
        internal static void ClientLeaveScoreScreen()
        {
            var ss = Object.FindObjectOfType<ScoreScreen>();
            if (ss != null && Game.Match != null && Game.Match.MatchState == MatchState.ScoreScreen) ss.Proceed();
        }

        private void OnRoundWinner(ref NetReader r)
        {
            int round = r.U8();
            var d = new RoundWinnerData { WinnerKind = r.U8(), Winner = r.U8() };
            int n = r.U8();
            for (int i = 0; i < n; i++) d.Scores[r.U8()] = r.I32();
            int nt = r.U8();
            for (int i = 0; i < nt; i++) d.TeamScores[r.U8()] = r.I32();
            string who = d.WinnerKind == WinPlayer ? "P" + (d.Winner + 1) : d.WinnerKind == WinTeam ? "team " + d.Winner : "none";
            Plugin.Trace($"Sync[C]: round {round} winner {who}");
            if (roundsAwaitingScore.Remove(round)) ApplyScores(d); // we already used a local guess
            else roundWinners[round] = d;
        }

        internal static void ApplyScores(RoundWinnerData d)
        {
            if (Game.Match == null) return;
            foreach (var p in Game.Match.Players)
                if (d.Scores.TryGetValue((byte)p.PlayerNumber, out int s) && p.Score != null && p.Score.Value != s) p.Score.Value = s;
            var teams = Game.Match.Teams;
            for (int i = 0; i < teams.Count; i++)
                if (d.TeamScores.TryGetValue((byte)i, out int ts) && teams[i].Score != null && teams[i].Score.Value != ts) teams[i].Score.Value = ts;
        }

        // ================= Merlinball =================

        internal void HostOnGoal(MerlinBallTarget target, Ball ball)
        {
            ushort id = HostBallId(ball);
            var w = net.Begin(Msg.Goal);
            w.U8((byte)target.TeamIndex);
            w.U16(id);
            w.Vec2(ball.Rigidbody2D.position);
            w.Vec2(ball.Direction);
            w.U8(OwnerNum(ball.Owner));
            net.Broadcast(w, true);
            Plugin.Trace($"Sync[H]: goal in target {target.TeamIndex} by P{OwnerNum(ball.Owner) + 1}");
        }

        private void OnGoal(ref NetReader r)
        {
            int teamIndex = r.U8();
            var ball = BallById(r.U16());
            Vector2 pos = r.Vec2(), dir = r.Vec2();
            byte owner = r.U8();
            MerlinBallTarget target = Level.MerlinBallTargets.Find(t => t != null && t.TeamIndex == teamIndex);
            if (target == null) { Plugin.Trace("Sync[C]: goal for unknown target " + teamIndex); return; }
            if (ball == null || !ball.isActiveAndEnabled)
            {
                // Our copy is gone already; a stand-in lets the goal play out natively.
                SpawningNetBall = true;
                try { ball = Staple.Scene.InstantiateInScene<Ball>(Game.GlobalResources.BallPrefab, pos, Quaternion.identity); }
                finally { SpawningNetBall = false; }
            }
            ball.transform.position = pos;
            ball.Rigidbody2D.position = pos;
            ball.Direction = dir;
            if (ball.State != Ball.BallState.Normal) ball.SetState(Ball.BallState.Normal);
            var pc = owner == NoPlayer ? null : ActorOf(owner);
            ball.Owner = pc != null ? pc.SpellOwner : null;
            Plugin.Trace($"Sync[C]: goal in target {teamIndex} by P{owner + 1}");
            using (Replay.Begin())
                Traverse.Create(target).Method("OnTriggerEnter2D", ball.CircleCollider2D).GetValue();
        }

        /// <summary>Client: host's result for this round, or null (caller then uses the local result and
        /// absolute scores get applied when the host's message lands).</summary>
        internal RoundWinnerData TakeRoundWinner(int round)
        {
            if (roundWinners.TryGetValue(round, out var d)) { roundWinners.Remove(round); return d; }
            roundsAwaitingScore.Add(round);
            return null;
        }

        private void OnCombatMessage(Msg msg, ref NetReader r)
        {
            switch (msg)
            {
                case Msg.BallSpawn: OnBallSpawn(ref r); break;
                case Msg.BallDespawn: OnBallDespawn(ref r); break;
                case Msg.Catch: OnCatch(ref r); break;
                case Msg.Launch: OnLaunch(ref r); break;
                case Msg.Release: OnRelease(ref r); break;
                case Msg.Damage:
                case Msg.Death:
                case Msg.Hex:
                    // Target may not exist locally yet (we spawn a moment after the host): retry shortly.
                    if (ActorOf(r.PeekU8()) == null) Defer(msg, r.RemainingCopy());
                    else Dispatch(msg, ref r);
                    break;
                case Msg.RoundWinner: OnRoundWinner(ref r); break;
                case Msg.PostMatch: ClientLeaveScoreScreen(); break;
                case Msg.Goal: OnGoal(ref r); break;
                case Msg.ShotDead: OnShotDead(ref r); break;
                case Msg.MatchStats: OnMatchStats(ref r); break;
                case Msg.Scores: OnScores(ref r); break;
                case Msg.Respawn: OnRespawn(ref r); break;
                case Msg.EntitySpawn: OnEntitySpawn(ref r); break;
                case Msg.EntityDespawn: OnEntityDespawn(ref r); break;
                case Msg.EntityDeath: OnEntityDeath(ref r); break;
                case Msg.Beast: OnBeast(ref r); break;
                case Msg.RefereeLine: RefereeLinePatch.PendingLine = r.Str(); break;
                case Msg.MatchAbort:
                    Plugin.Trace("Sync[C]: host ended the match");
                    if (Game.Match != null) Game.Match.QuitToMainMenu();
                    break;
                case Msg.ItemSpawn: OnItemSpawn(ref r); break;
                case Msg.ItemDespawn: OnItemDespawn(ref r); break;
                case Msg.ItemPickup: OnItemPickup(ref r); break;
                case Msg.FastForward:
                    fastForwardRound = r.U8();
                    ForcedSkipPressPatch.Pending = true;
                    break;
                case Msg.RoundOver:
                    roundsOver.Add(r.U8());
                    break;
            }
        }
    }
}
