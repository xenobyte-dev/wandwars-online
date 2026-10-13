using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Lag-compensated catching for online players. A client's own swing catches as soon as the ball
    /// touches it on that client's screen (the wizard freezes right there, as in local play) and the
    /// client claims the catch. The host checks the claim against where the ball recently was and, if
    /// it fits, applies the catch at the client's spot; otherwise it tells the client to undo it.
    /// The host's own physics no longer decides catches for online players.
    /// </summary>
    public partial class MatchSync
    {
        /// <summary>Set while the host applies a client's claimed catch.</summary>
        internal static bool ApplyingClaim;

        private const float ClaimTimeout = 1.5f;
        private const float BallHistorySeconds = 1f;

        // ---- client ----

        private readonly Dictionary<Ball, float> predictedCatches = new Dictionary<Ball, float>();

        internal bool HasPredictedCatch => predictedCatches.Count > 0;
        public int PredictedCatchCount { get; private set; }

        /// <summary>Client: is this our own wizard's catch spell (the one we predict)?</summary>
        internal bool IsLocalCatchSpell(CatchSpell cs)
        {
            if (!IsClient || !Predict || localNum == NoPlayer || cs == null) return false;
            var pc = EntityOf(localNum);
            return pc != null && pc.CatchSpell == cs;
        }

        private readonly Dictionary<Ball, float> rejectedAt = new Dictionary<Ball, float>();

        /// <summary>Client: just rejected for this ball? Don't keep re-claiming it every frame.</summary>
        internal bool RecentlyRejected(Ball ball) =>
            ball != null && rejectedAt.TryGetValue(ball, out float t) && Time.realtimeSinceStartup - t < 0.3f;

        internal void ClientOnPredictedCatch(CatchSpell cs, CatchSpell.BallInfo info, Vector2 offset)
        {
            if (info?.Ball == null || !ballIds.TryGetValue(info.Ball, out ushort id)) return;
            predictedCatches[info.Ball] = Time.realtimeSinceStartup;
            PredictedCatchCount++;
            if (Dev.DevMode.Enabled) Plugin.Trace($"Sync[C]: predicted catch ball {id} at {(Vector2)cs.transform.position}");
            var w = net.Begin(Msg.CatchClaim);
            w.U16(id);
            w.Vec2(offset);
            w.Vec2(info.PreCatchVelocity);
            w.Vec2(cs.transform.position);
            net.SendTo(0, w, true);
        }

        private void OnCatchReject(ref NetReader r)
        {
            var ball = BallById(r.U16());
            if (ball != null && predictedCatches.ContainsKey(ball))
            {
                Plugin.Trace("Sync[C]: catch rejected by host");
                rejectedAt[ball] = Time.realtimeSinceStartup;
                UndoPredictedCatch(ball);
            }
        }

        /// <summary>Client: take back a catch the host didn't confirm; snapshots then move the ball.</summary>
        private void UndoPredictedCatch(Ball ball)
        {
            predictedCatches.Remove(ball);
            if (ball == null) return;
            var pc = EntityOf(localNum);
            var cs = pc != null ? pc.CatchSpell : null;
            if (cs != null) cs.HeldBalls.RemoveAll(x => x.Ball == ball);
            ball.UnlockOwner();
            ball.CurrentCatchSpell = null;
            ball.SetState(Ball.BallState.Normal);
        }

        private readonly List<Ball> scratchPredicted = new List<Ball>();

        private void ClientUpdatePredictedCatches()
        {
            if (predictedCatches.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            scratchPredicted.Clear();
            foreach (var kv in predictedCatches)
                if (kv.Key == null || now - kv.Value > ClaimTimeout) scratchPredicted.Add(kv.Key);
            foreach (var b in scratchPredicted) UndoPredictedCatch(b);
        }

        // ---- host ----

        private struct BallSample { public float Time; public Vector2 Pos; }

        private readonly Dictionary<Ball, List<BallSample>> ballHistory = new Dictionary<Ball, List<BallSample>>();

        private void HostRecordBallHistory()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in ballIds)
            {
                var b = kv.Key;
                if (b == null) continue;
                if (!ballHistory.TryGetValue(b, out var list)) ballHistory[b] = list = new List<BallSample>();
                list.Add(new BallSample { Time = now, Pos = b.Rigidbody2D.position });
                int drop = 0;
                while (drop < list.Count && now - list[drop].Time > BallHistorySeconds) drop++;
                if (drop > 0) list.RemoveRange(0, drop);
            }
            if (ballHistory.Count > ballIds.Count + 8)
            {
                var dead = new List<Ball>();
                foreach (var b in ballHistory.Keys) if (b == null || !ballIds.ContainsKey(b)) dead.Add(b);
                foreach (var b in dead) ballHistory.Remove(b);
            }
        }

        /// <summary>Host: catches for online players come from their claims, not the host's physics.</summary>
        internal bool IsRemoteHumanCatchSpell(CatchSpell cs)
        {
            if (!IsHost || cs == null || cs.Owner == null) return false;
            byte num = OwnerNum(cs.Owner);
            if (num == NoPlayer) return false;
            var p = PlayerOf(num);
            return p != null && !p.IsBot && p.InputDevice is NetInputDevice;
        }

        private struct PendingClaim
        {
            public int Peer; public ushort Id; public Vector2 Offset, PreVel, CatcherPos; public float Since;
        }

        /// <summary>Claims whose swing input hasn't reached the host yet (wait briefly for it).</summary>
        private readonly List<PendingClaim> pendingClaims = new List<PendingClaim>();
        private const float ClaimWaitForSwing = 0.35f;

        private void HostOnCatchClaim(int peer, ref NetReader r)
        {
            var c = new PendingClaim { Peer = peer, Id = r.U16(), Offset = r.Vec2(), PreVel = r.Vec2(), CatcherPos = r.Vec2(), Since = Time.realtimeSinceStartup };
            if (!TryApplyClaim(c, false)) pendingClaims.Add(c);
        }

        private void HostUpdatePendingClaims()
        {
            for (int i = pendingClaims.Count - 1; i >= 0; i--)
            {
                var c = pendingClaims[i];
                bool last = Time.realtimeSinceStartup - c.Since > ClaimWaitForSwing;
                if (TryApplyClaim(c, last) || last) pendingClaims.RemoveAt(i);
            }
        }

        /// <summary>Returns false to keep waiting (only while the catcher's swing hasn't started).</summary>
        private bool TryApplyClaim(PendingClaim c, bool final)
        {
            int peer = c.Peer;
            ushort id = c.Id;
            Vector2 offset = c.Offset, preVel = c.PreVel, catcherPos = c.CatcherPos;
            if (!net.Remotes.TryGetValue(peer, out var rp) || Game.Match == null) return true;
            Player player = null;
            foreach (var p in Game.Match.Players)
                if (p.InputDevice == rp.Device) { player = p; break; }
            var pc = player != null ? ActiveEntity(player) as PlayerController : null;
            var ball = BallById(id);
            string why = ValidateClaim(peer, pc, ball, catcherPos + offset, Time.realtimeSinceStartup - c.Since);
            if (Dev.DevMode.Enabled && ball != null) Plugin.Trace($"Sync[H]: claim error {Vector2.Distance(ball.Rigidbody2D.position, catcherPos + offset):F2} (ball speed {ball.Rigidbody2D.velocity.magnitude:F0})");
            if (why != null && why.StartsWith("not swinging") && !final) return false;
            if (why == null)
            {
                var cs = pc.CatchSpell;
                Vector2 hostPosBefore = pc.Rigidbody2D.position;
                // Freeze where the catcher saw the catch (both sides then hold still at the same spot).
                pc.Rigidbody2D.position = catcherPos;
                pc.transform.position = catcherPos;
                pc.Rigidbody2D.velocity = Vector2.zero;
                ball.UnlockOwner();
                ball.LastCatchTimeStamp = -1f;
                Vector2 bp = (Vector2)cs.transform.position + offset;
                ball.transform.position = bp;
                ball.Rigidbody2D.position = bp;
                ball.Rigidbody2D.velocity = preVel;
                int before = cs.HeldBalls.Count;
                ApplyingClaim = true;
                try { Traverse.Create(cs).Method("HandleCatch", new CatchSpell.BallInfo(ball)).GetValue(); }
                finally { ApplyingClaim = false; }
                if (cs.HeldBalls.Count > before)
                {
                    if (Dev.DevMode.Enabled) Plugin.Trace($"Sync[H]: catch claim accepted (ball {id}, host copy moved {Vector2.Distance(hostPosBefore, catcherPos):F2})");
                    return true; // HandleCatchPatch broadcast the catch
                }
                why = "local checks";
            }
            if (Dev.DevMode.Enabled) Plugin.Trace($"Sync[H]: catch claim rejected ({why}) ball {id} known={string.Join(",", ballsById.Keys.Select(k => k.ToString()).ToArray())}");
            var w = net.Begin(Msg.CatchReject);
            w.U16(id);
            net.SendTo(peer, w, true);
            return true;
        }

        private string ValidateClaim(int peer, PlayerController pc, Ball ball, Vector2 claimedBallPos, float waited)
        {
            if (pc == null || !pc.gameObject.activeInHierarchy || pc.CatchSpell == null) return "no catcher";
            if (ball == null) return "no ball (unknown id)";
            if (!ball.gameObject.activeInHierarchy) return "no ball (inactive, state " + ball.State + ")";
            if (ball.OwnerLock || ball.CurrentCatchSpell != null) return "ball already held";
            var cs = pc.CatchSpell;
            if (cs.State == CatchSpell.CatchSpellState.Ready && pc.CanCast)
                pc.AbilityOne(); // the swing input hasn't arrived yet (late or lost); it's implied by the claim
            if (cs.State != CatchSpell.CatchSpellState.Casting) return "not swinging (" + cs.State + ")";
            // The ball must have been near the claimed spot within the catcher's round trip (+ slack).
            float rtt = Mathf.Max(0f, net.GetRtt(peer)) / 1000f;
            float window = Mathf.Min(rtt + 0.2f + waited, BallHistorySeconds);
            float tolerance = 2f + ball.Rigidbody2D.velocity.magnitude * 0.04f;
            float now = Time.realtimeSinceStartup;
            if (ballHistory.TryGetValue(ball, out var list))
                for (int i = list.Count - 1; i >= 0 && now - list[i].Time <= window; i--)
                    if (Vector2.Distance(list[i].Pos, claimedBallPos) <= tolerance) return null;
            if (Vector2.Distance(ball.Rigidbody2D.position, claimedBallPos) <= tolerance) return null;
            return "ball too far";
        }
        // ---- host: catcher's grace on ball hits ----

        /// <summary>Set while the host applies a hit it held back.</summary>
        internal static bool ApplyingDeferredHit;

        private struct DeferredHit { public GameEntity Target; public Ball Ball; public int Amount; public float Due; }

        private readonly List<DeferredHit> deferredHits = new List<DeferredHit>();

        /// <summary>
        /// Host: a ball hits an online player. Their catch of that ball may still be on its way (they see
        /// the ball later than the host does), so hold the hit for about half their round trip; a catch
        /// claim for this ball that gets accepted in the meantime cancels it.
        /// </summary>
        internal bool HostDeferBallHit(GameEntity target, int amount, GameObject source)
        {
            if (!IsHost || ApplyingDeferredHit || source == null || target == null) return false;
            var ball = source.GetComponent<Ball>();
            if (ball == null) return false;
            byte num = PlayerNum(target);
            if (num == NoPlayer) return false;
            var p = PlayerOf(num);
            if (p == null || p.IsBot || !(p.InputDevice is NetInputDevice)) return false;
            foreach (var d in deferredHits)
                if (d.Target == target && d.Ball == ball) return true; // already pending
            float rtt = 0f;
            foreach (var rp in net.Remotes.Values)
                if (rp.Device == p.InputDevice) rtt = Mathf.Max(0f, net.GetRtt(rp.Peer)) / 1000f;
            float grace = Mathf.Clamp(rtt * 0.5f + 0.08f, 0.08f, 0.2f);
            deferredHits.Add(new DeferredHit { Target = target, Ball = ball, Amount = amount, Due = Time.realtimeSinceStartup + grace });
            return true;
        }

        private void HostUpdateDeferredHits()
        {
            if (deferredHits.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = deferredHits.Count - 1; i >= 0; i--)
            {
                var d = deferredHits[i];
                if (d.Due > now) continue;
                deferredHits.RemoveAt(i);
                if (d.Target == null || !d.Target.isActiveAndEnabled || d.Ball == null) continue;
                var pc = d.Target as PlayerController;
                bool caught = pc != null && pc.CatchSpell != null && pc.CatchSpell.HeldBalls.Exists(x => x.Ball == d.Ball);
                if (caught)
                {
                    if (Dev.DevMode.Enabled) Plugin.Trace("Sync[H]: held-back hit cancelled by catch");
                    continue;
                }
                ApplyingDeferredHit = true;
                try { d.Target.TakeDamage(d.Amount, d.Ball.gameObject); }
                finally { ApplyingDeferredHit = false; }
            }
        }
    }
}
