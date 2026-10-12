using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Staple;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Medley and Eggnarok chain sub-matches, choosing the next mode/stage/teams randomly. The host decides
    /// and broadcasts each one; clients park in an AwaitHostMatch state and build exactly that sub-match
    /// from the same Player objects (scores carried over as the host has them).
    /// </summary>
    public sealed partial class MatchSync
    {
        private int hostSubIndex, clientSubConsumed;
        private readonly Dictionary<int, SubMatchData> subMatches = new Dictionary<int, SubMatchData>();

        internal sealed class SubMatchData
        {
            public string TypeName, Level;
            public MatchType Type;
            public int ScoreLimit, EggMatchNumber;
            public bool IsMedley;
            public readonly List<SubPlayer> Players = new List<SubPlayer>();
        }

        internal sealed class SubPlayer
        {
            public byte Num;
            public int Team, Score, Medley;
        }

        private void ResetSubMatches()
        {
            hostSubIndex = 0;
            clientSubConsumed = 0;
            subMatches.Clear();
        }

        /// <summary>Both sides: fresh per-match sync state when a chained sub-match begins.</summary>
        internal void ResetForSubMatch()
        {
            roundSpawns.Clear();
            clientMoves.Clear();
            hostMoves.Clear();
            ResetCombat();
            ResetItems();
            ResetStats();
            ResetEntities();
            lastScoreState.Clear();
            haveFrame = haveSnapshot = false;
        }

        internal void HostOnSubMatch(MatchBase next, List<Player> players, MatchConfig config)
        {
            ResetForSubMatch();
            hostSubIndex++;
            var teams = Game.GlobalResources.Teams;
            var w = net.Begin(Msg.SubMatch);
            w.U16((ushort)hostSubIndex);
            w.Str(next.GetType().FullName);
            w.Str(next.LevelName ?? "");
            w.U8((byte)config.MatchType);
            w.I32(config.ScoreLimit);
            w.Bool(config.IsMedleyMode);
            w.I32(Eggnarok.CurrentMatchNumber);
            w.U8((byte)players.Count);
            foreach (var p in players)
            {
                w.U8((byte)p.PlayerNumber);
                w.I16((short)(p.Team != null ? teams.IndexOf(p.Team) : -1));
                w.I32(p.Score != null ? p.Score.Value : 0);
                w.I32(p.MedleyScore != null ? p.MedleyScore.Value : 0);
            }
            net.Broadcast(w, true);
            Plugin.Trace($"Sync[H]: sub-match {hostSubIndex}: {config.MatchType} on '{next.LevelName}'");
        }

        private void OnSubMatch(ref NetReader r)
        {
            int index = r.U16();
            var d = new SubMatchData
            {
                TypeName = r.Str(),
                Level = r.Str(),
                Type = (MatchType)r.U8(),
                ScoreLimit = r.I32(),
                IsMedley = r.Bool(),
                EggMatchNumber = r.I32(),
            };
            int n = r.U8();
            for (int i = 0; i < n; i++)
                d.Players.Add(new SubPlayer { Num = r.U8(), Team = r.I16(), Score = r.I32(), Medley = r.I32() });
            subMatches[index] = d;
        }

        internal SubMatchData TakeNextSubMatch()
        {
            if (!subMatches.TryGetValue(clientSubConsumed + 1, out var d)) return null;
            subMatches.Remove(clientSubConsumed + 1);
            clientSubConsumed++;
            return d;
        }
    }

    /// <summary>Client placeholder between chained sub-matches: waits for the host, then builds its pick.</summary>
    public sealed class AwaitHostMatch : MatchBase
    {
        private const float GiveUpSeconds = 30f;

        public AwaitHostMatch(List<Player> players, MatchConfig config) : base(string.Empty, players, config)
        {
        }

        public override void Start()
        {
            Transition = null;
        }

        public override IEnumerator Run()
        {
            float deadline = Time.realtimeSinceStartup + GiveUpSeconds;
            MatchSync.SubMatchData d;
            while ((d = MatchSync.Instance.TakeNextSubMatch()) == null)
            {
                if (Time.realtimeSinceStartup > deadline || !MatchSync.IsClient)
                {
                    Plugin.Trace("Sync[C]: host never sent the next sub-match; back to menu");
                    NextState = new GameLobby("GameLobby");
                    Quit = true;
                    yield break;
                }
                yield return null;
            }

            var config = Config;
            config.MatchType = d.Type;
            config.ScoreLimit = d.ScoreLimit;
            config.IsMedleyMode = d.IsMedley;
            Traverse.Create(typeof(Eggnarok)).Field("currentMatchNumber").SetValue(d.EggMatchNumber);
            var teams = Game.GlobalResources.Teams;
            foreach (var sp in d.Players)
            {
                var p = players.Find(x => (byte)x.PlayerNumber == sp.Num);
                if (p == null) continue;
                p.Team = sp.Team >= 0 && sp.Team < teams.Count ? teams[sp.Team] : null;
                if (p.Score != null) p.Score.Value = sp.Score;
                if (p.MedleyScore != null) p.MedleyScore.Value = sp.Medley;
            }

            var type = typeof(MatchBase).Assembly.GetType(d.TypeName);
            MatchSync.Instance.ResetForSubMatch();
            var next = Activator.CreateInstance(type, d.Level, players, config) as MatchBase;
            Plugin.Trace($"Sync[C]: sub-match {d.Type} on '{d.Level}'");
            NextState = next;
            NextState.Transition = Transitions.GetTransitionOfType(TransitionType.Fade);
            Quit = true;
        }

        public override void End()
        {
        }
    }

    [HarmonyPatch]
    internal static class SubMatchPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Medley), nameof(Medley.GetNextState));
            yield return AccessTools.Method(typeof(Eggnarok), nameof(Eggnarok.GetNextState));
        }

        private static bool Prefix(List<Player> players, MatchConfig config, ref MatchBase __result)
        {
            if (!MatchSync.IsClient) return true;
            __result = new AwaitHostMatch(players, config);
            return false;
        }

        private static void Postfix(List<Player> players, MatchConfig config, MatchBase __result)
        {
            if (MatchSync.IsHost && __result != null && !(__result is AwaitHostMatch))
                MatchSync.Instance.HostOnSubMatch(__result, players, config);
        }
    }
}
