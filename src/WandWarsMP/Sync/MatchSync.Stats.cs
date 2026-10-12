using System.Collections.Generic;
using HarmonyLib;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// End-of-match statistics (score screen panels): the host's numbers are authoritative. Clients compute
    /// their own first (ProcessStatistics) and then overwrite with the host's when they arrive.
    /// </summary>
    public sealed partial class MatchSync
    {
        private sealed class PlayerStats
        {
            public int Kills, Deaths, Suicides, Goals, HexesHit, HexesCast, Hexed, Points, TotalChickens, Medley;
            public float Posession, PosessionNorm;
        }

        private Dictionary<byte, PlayerStats> pendingStats;

        internal void HostSendStats(MatchBase match)
        {
            var w = net.Begin(Msg.MatchStats);
            w.U8((byte)match.Players.Count);
            foreach (var p in match.Players)
            {
                w.U8((byte)p.PlayerNumber);
                w.I32(p.Kills.Count);
                w.I32(p.Deaths);
                w.I32(p.Suicides);
                w.I32(p.Goals);
                w.I32(p.HexesHit);
                w.I32(p.HexesCast);
                w.I32(p.Hexed);
                w.I32(p.Points);
                w.I32(p.TotalChickensEaten);
                w.I32(p.MedleyScore != null ? p.MedleyScore.Value : 0);
                w.F32(p.PosessionTime);
                w.F32(p.PosessionNormalized);
            }
            net.Broadcast(w, true);
        }

        private void OnMatchStats(ref NetReader r)
        {
            int n = r.U8();
            var stats = new Dictionary<byte, PlayerStats>();
            for (int i = 0; i < n; i++)
            {
                byte num = r.U8();
                stats[num] = new PlayerStats
                {
                    Kills = r.I32(), Deaths = r.I32(), Suicides = r.I32(), Goals = r.I32(), HexesHit = r.I32(),
                    HexesCast = r.I32(), Hexed = r.I32(), Points = r.I32(), TotalChickens = r.I32(), Medley = r.I32(),
                    Posession = r.F32(), PosessionNorm = r.F32(),
                };
            }
            pendingStats = stats;
            ApplyStats(); // no-op until the local match has processed its own stats
        }

        private bool localStatsDone;

        internal void ClientLocalStatsDone()
        {
            localStatsDone = true;
            ApplyStats();
        }

        private void ApplyStats()
        {
            var m = Game.Match;
            if (pendingStats == null || m == null || !localStatsDone) return;
            foreach (var p in m.Players)
            {
                if (!pendingStats.TryGetValue((byte)p.PlayerNumber, out var s)) continue;
                while (p.Kills.Count > s.Kills) p.Kills.RemoveAt(p.Kills.Count - 1);
                while (p.Kills.Count < s.Kills) p.Kills.Add(new KillInfo { Killer = p });
                p.Deaths = s.Deaths;
                p.Suicides = s.Suicides;
                p.Goals = s.Goals;
                p.HexesHit = s.HexesHit;
                p.HexesCast = s.HexesCast;
                p.Hexed = s.Hexed;
                p.Points = s.Points;
                p.TotalChickensEaten = s.TotalChickens;
                if (p.MedleyScore != null && p.MedleyScore.Value != s.Medley) p.MedleyScore.Value = s.Medley;
                p.PosessionTime = s.Posession;
                p.PosessionNormalized = s.PosessionNorm;
            }
            pendingStats = null;
        }

        private void ResetStats()
        {
            pendingStats = null;
            localStatsDone = false;
        }
    }

    [HarmonyPatch(typeof(MatchBase), "ProcessStatistics")]
    internal static class ProcessStatisticsPatch
    {
        private static void Postfix(MatchBase __instance)
        {
            if (MatchSync.IsHost) MatchSync.Instance.HostSendStats(__instance);
            else if (MatchSync.IsClient) MatchSync.Instance.ClientLocalStatsDone();
        }
    }
}
