using System.Collections.Generic;
using UnityEngine;
using WandWars;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Client time alignment. A client's own wizard runs "now" (prediction), but host state arrives half
    /// a round trip old, and the client's actions reach the host half a round trip late. So the client
    /// shows the ball (and other wizards) one full round trip ahead of the last host state: exactly where
    /// the host will have them when the client's current input arrives. Dodges, catches and throws then
    /// land on the host the way they looked on the client's screen.
    /// </summary>
    public sealed partial class MatchSync
    {
        /// <summary>How far ahead of received host state the client displays moving objects (s).</summary>
        internal float DisplayLead => IsClient && Predict && net != null ? Mathf.Clamp(net.GetRtt(0) / 1000f, 0f, 0.25f) : 0f;

        private struct PosSample { public float Time; public Vector2 Pos; }

        private readonly Dictionary<Ball, List<PosSample>> clientBallHistory = new Dictionary<Ball, List<PosSample>>();
        private readonly HashSet<Ball> ownLaunched = new HashSet<Ball>();
        private readonly List<Ball> scratchHistory = new List<Ball>();
        private int corrN, snapNoHist, snapAngle, snapFar;
        private float corrSum, nextCorrTrace;

        private void ClientRecordBallHistory()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in ballsById)
            {
                var b = kv.Value;
                if (b == null) continue;
                if (!clientBallHistory.TryGetValue(b, out var list)) clientBallHistory[b] = list = new List<PosSample>();
                list.Add(new PosSample { Time = now, Pos = b.Rigidbody2D.position });
                int drop = 0;
                while (drop < list.Count - 2 && now - list[drop].Time > 0.6f) drop++;
                if (drop > 0) list.RemoveRange(0, drop);
            }
            if (clientBallHistory.Count > ballsById.Count + 4)
            {
                scratchHistory.Clear();
                foreach (var b in clientBallHistory.Keys) if (b == null || !ballIds.ContainsKey(b)) scratchHistory.Add(b);
                foreach (var b in scratchHistory) clientBallHistory.Remove(b);
            }
        }

        /// <summary>Where this ball was on our screen at time t (null if we have no record that old).</summary>
        private Vector2? BallHistoryAt(Ball b, float t)
        {
            if (!clientBallHistory.TryGetValue(b, out var list) || list.Count == 0 || list[0].Time > t) return null;
            for (int i = list.Count - 1; i > 0; i--)
            {
                if (list[i - 1].Time > t) continue;
                var a = list[i - 1]; var c = list[i];
                float span = c.Time - a.Time;
                return span <= 0f ? c.Pos : Vector2.Lerp(a.Pos, c.Pos, (t - a.Time) / span);
            }
            return list[list.Count - 1].Pos;
        }

        /// <summary>
        /// Moves a ball state forward in time like its physics would: straight line, bouncing off solid
        /// level geometry (not players, balls or triggers). Returns the new position and velocity.
        /// </summary>
        internal static Vector2 Extrapolate(Ball b, Vector2 pos, ref Vector2 vel, float t)
        {
            float speed = vel.magnitude;
            if (t <= 0f || speed < 0.01f) return pos;
            float radius = b != null && b.CircleCollider2D != null ? b.CircleCollider2D.radius * Mathf.Abs(b.transform.lossyScale.x) : 0.3f;
            int mask = SolidMaskFor(b != null ? b.gameObject.layer : 10);
            bool hitTriggers = Physics2D.queriesHitTriggers;
            Physics2D.queriesHitTriggers = false;
            try
            {
                float remaining = speed * t;
                Vector2 dir = vel / speed;
                for (int bounce = 0; bounce < 3 && remaining > 0.001f; bounce++)
                {
                    var hit = Physics2D.CircleCast(pos, radius, dir, remaining, mask);
                    if (hit.collider == null) { pos += dir * remaining; break; }
                    float travel = Mathf.Max(0f, hit.distance - 0.01f);
                    pos += dir * travel;
                    remaining -= travel;
                    dir = Vector2.Reflect(dir, hit.normal);
                    if (travel < 0.001f && bounce > 0) break; // wedged: stop here
                }
                vel = dir * speed;
                return pos;
            }
            finally { Physics2D.queriesHitTriggers = hitTriggers; }
        }

        private static readonly Dictionary<int, int> solidMasks = new Dictionary<int, int>();

        /// <summary>Layers this layer physically collides with, minus players (8) and balls (10).</summary>
        private static int SolidMaskFor(int layer)
        {
            if (solidMasks.TryGetValue(layer, out int mask)) return mask;
            mask = 0;
            for (int i = 0; i < 32; i++)
                if (!Physics2D.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
            mask &= ~((1 << 8) | (1 << 10));
            solidMasks[layer] = mask;
            return mask;
        }

        /// <summary>
        /// Client: correct a free ball with a host snapshot. The host position is for the time we showed
        /// one lead ago, so compare against our own record of that moment and shift by the difference
        /// (bounces since then are already in our local simulation).
        /// </summary>
        private void ClientCorrectBall(Ball b, Vector2 hostPos, Vector2 hostVel)
        {
            var rb = b.Rigidbody2D;
            float lead = DisplayLead;
            float now = Time.realtimeSinceStartup;
            Vector2? recorded = BallHistoryAt(b, now - lead);
            // No record that old (just thrown/caught): assume a straight line back from where it is now.
            Vector2 then = recorded ?? rb.position - rb.velocity * lead;
            Vector2? before = recorded.HasValue ? BallHistoryAt(b, now - lead - 0.05f) : null;
            Vector2 err = hostPos - then;
            bool diverged = false;
            if (before != null && hostVel.sqrMagnitude > 1f && err.magnitude > 1f)
            {
                // Heading differs from the host's at that moment and we're off: it hit something we didn't see.
                Vector2 ourVel = (then - before.Value) / 0.05f;
                if (ourVel.sqrMagnitude > 1f && Vector2.Angle(ourVel, hostVel) > 35f) diverged = true;
            }
            float snapAt = 2.5f + hostVel.magnitude * 0.05f;
            corrN++; corrSum += err.magnitude;
            if (recorded == null) snapNoHist++; if (diverged) snapAngle++; else if (err.magnitude > snapAt) snapFar++;
            if (Dev.DevMode.Enabled && Time.unscaledTime > nextCorrTrace)
            {
                nextCorrTrace = Time.unscaledTime + 2f;
                Plugin.Trace($"Sync[C]: ball corr avg {corrSum / Mathf.Max(1, corrN):F2} lead {lead * 1000f:F0}ms nohist={snapNoHist} snaps angle={snapAngle} far={snapFar} of {corrN}");
                corrN = 0; corrSum = 0; snapNoHist = snapAngle = snapFar = 0;
            }
            if (diverged || err.magnitude > snapAt)
            {
                Vector2 v = hostVel;
                rb.position = Extrapolate(b, hostPos, ref v, lead);
                rb.velocity = v;
                Snaps++;
                return;
            }
            rb.position += err * PosBlend;
        }

        /// <summary>Client: a ball just left someone's hand (message is half a round trip old): catch up.</summary>
        private void ClientAdvanceReleasedBall(Ball b)
        {
            if (b == null || b.CurrentCatchSpell != null) return;
            var rb = b.Rigidbody2D;
            Vector2 v = rb.velocity;
            rb.position = Extrapolate(b, rb.position, ref v, DisplayLead);
            rb.velocity = v;
            if (clientBallHistory.TryGetValue(b, out var list)) list.Clear();
        }

        /// <summary>Client: our own throw happens on our own timer (host launches at the matching time).</summary>
        internal bool IsOwnPredictedLaunch(CatchSpell cs) => IsLocalCatchSpell(cs);

        internal void ClientOnOwnLaunch(Ball b)
        {
            if (b == null) return;
            ownLaunched.Add(b);
            if (clientBallHistory.TryGetValue(b, out var list)) list.Clear();
        }

        /// <summary>Remote wizards: show them where the host will have them (bounded, they can turn).</summary>
        private Vector2 RemoteTarget(Vector2 hostPos, Vector2 hostVel) =>
            hostPos + hostVel * Mathf.Min(DisplayLead, 0.12f);

        private void ResetTimeAlign()
        {
            clientBallHistory.Clear();
            ownLaunched.Clear();
        }
    }
}
