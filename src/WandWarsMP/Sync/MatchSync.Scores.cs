using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Generic score stream and mid-round respawns.
    /// Scores: the host polls every player's Score / ChickensEaten / Lives and every team's Score and sends
    /// absolute values whenever anything changes; clients overwrite theirs (HUDs listening to the score
    /// objects update themselves; Hexout's HUD is refreshed explicitly).
    /// Respawns: any SpawnCharacter call without an explicit spawn point picks a random point; on clients
    /// such calls are blocked and replayed from the host with the host's chosen point.
    /// </summary>
    public sealed partial class MatchSync
    {
        private readonly List<int> lastScoreState = new List<int>();
        private readonly List<int> scoreScratch = new List<int>();

        private void HostTrackScores()
        {
            var m = Game.Match;
            scoreScratch.Clear();
            foreach (var p in m.Players)
            {
                scoreScratch.Add(p.Score != null ? p.Score.Value : 0);
                scoreScratch.Add(p.ChickensEaten != null ? p.ChickensEaten.Value : 0);
                scoreScratch.Add(p.Lives);
            }
            foreach (var t in m.Teams) scoreScratch.Add(t.Score != null ? t.Score.Value : 0);
            if (scoreScratch.Count == lastScoreState.Count)
            {
                bool same = true;
                for (int i = 0; i < scoreScratch.Count && same; i++) same = scoreScratch[i] == lastScoreState[i];
                if (same) return;
            }
            lastScoreState.Clear();
            lastScoreState.AddRange(scoreScratch);

            var w = net.Begin(Msg.Scores);
            w.U8((byte)m.Players.Count);
            foreach (var p in m.Players)
            {
                w.U8((byte)p.PlayerNumber);
                w.I32(p.Score != null ? p.Score.Value : 0);
                w.I32(p.ChickensEaten != null ? p.ChickensEaten.Value : 0);
                w.I32(p.Lives);
            }
            w.U8((byte)m.Teams.Count);
            foreach (var t in m.Teams) w.I32(t.Score != null ? t.Score.Value : 0);
            net.Broadcast(w, true);
        }

        private void OnScores(ref NetReader r)
        {
            var m = Game.Match;
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                var p = PlayerOf(r.U8());
                int score = r.I32(), chickens = r.I32(), lives = r.I32();
                if (p == null) continue;
                if (p.Score != null && p.Score.Value != score) p.Score.Value = score;
                if (p.ChickensEaten != null && p.ChickensEaten.Value != chickens) p.ChickensEaten.Value = chickens;
                p.Lives = lives;
            }
            int nt = r.U8();
            for (int i = 0; i < nt; i++)
            {
                int v = r.I32();
                if (m != null && i < m.Teams.Count && m.Teams[i].Score != null && m.Teams[i].Score.Value != v) m.Teams[i].Score.Value = v;
            }
            // Hexout drives its HUD by explicit IncrementScore calls (diffs against the absolute score).
            var hud = Object.FindObjectOfType<HexoutHud>();
            if (hud != null && hud.Modules != null)
                foreach (var mod in hud.Modules)
                    if (mod != null && mod.gameObject.activeInHierarchy) mod.IncrementScore();
        }

        // ---- respawns ----

        internal void HostOnRespawn(MatchBase match, Player player, PlayerSpawnPoint given, bool spawnBall, bool activateImmunity)
        {
            var spawn = match.PlayerSpawn;
            var point = given != null ? given : (spawn != null ? spawn.LastUsedSpawnPoint : null);
            int idx = spawn != null && point != null ? System.Array.IndexOf(spawn.SpawnPoints, point) : -1;
            if (idx < 0) return;
            var w = net.Begin(Msg.Respawn);
            w.U8((byte)player.PlayerNumber);
            w.U8((byte)idx);
            w.Bool(spawnBall);
            w.Bool(activateImmunity);
            net.Broadcast(w, true);
        }

        private void OnRespawn(ref NetReader r)
        {
            var p = PlayerOf(r.U8());
            int idx = r.U8();
            bool spawnBall = r.Bool(), immunity = r.Bool();
            var m = Game.Match;
            if (p == null || m == null || m.PlayerSpawn == null || idx >= m.PlayerSpawn.SpawnPoints.Length) return;
            using (Replay.Begin())
                m.SpawnCharacter(p, spawnBall, false, m.PlayerSpawn.SpawnPoints[idx], immunity);
        }
    }

    /// <summary>
    /// Marks code running inside a round's spawn sequence (any mode's Spawn coroutine). SpawnCharacter
    /// calls outside it are mid-round respawns: host-decided, replayed on clients.
    /// </summary>
    [HarmonyPatch]
    internal static class RoundSpawnScopePatch
    {
        internal static int Depth;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("Spawn");

        private static void Postfix(ref System.Collections.IEnumerator __result)
        {
            if (MatchSync.Online && __result != null) __result = Scoped(__result);
        }

        private static System.Collections.IEnumerator Scoped(System.Collections.IEnumerator inner)
        {
            while (true)
            {
                bool more;
                Depth++;
                try { more = inner.MoveNext(); }
                finally { Depth--; }
                if (!more) yield break;
                yield return inner.Current;
            }
        }
    }

    /// <summary>All SpawnCharacter implementations (base + mode overrides).</summary>
    [HarmonyPatch]
    internal static class SpawnCharacterPatch
    {
        private static int depth;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("SpawnCharacter");

        private static bool IsRespawn => RoundSpawnScopePatch.Depth == 0 && Game.Match != null;

        private static bool Prefix(out bool __state)
        {
            __state = depth++ == 0;
            // Client: mid-round respawns come from the host instead.
            if (__state && IsRespawn && MatchSync.IsClient && !Replay.Active)
            {
                depth--;
                __state = false;
                return false;
            }
            return true;
        }

        private static void Postfix(MatchBase __instance, Player player, bool spawnBall, PlayerSpawnPoint spawnPoint, bool activateImmunity, bool __state, bool __runOriginal)
        {
            if (!__runOriginal) return;
            depth--;
            if (__state && IsRespawn && MatchSync.IsHost)
                MatchSync.Instance.HostOnRespawn(__instance, player, spawnPoint, spawnBall, activateImmunity);
        }
    }

    /// <summary>Hexout scoring happens per kill on the host; clients get it through the score stream.</summary>
    [HarmonyPatch]
    internal static class HexoutDeathPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(HexOut), "OnPlayerDeath");
            yield return AccessTools.DeclaredMethod(typeof(TeamHexout), "OnPlayerDeath");
        }

        private static bool Prefix() => !MatchSync.IsClient;
    }
}
