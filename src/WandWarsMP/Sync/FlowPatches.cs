using System.Collections;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Client: once a round has ended, hold RunRound before it picks a winner until the host's
    /// RoundWinner arrives (the host decides after its own 1s grace, so it always lands slightly after
    /// the client's grace). Without this the client guesses, can miss match end, and stalls.
    /// </summary>
    [HarmonyPatch(typeof(MatchBase), "RunRound")]
    internal static class RunRoundPatch
    {
        private const float MaxWait = 5f;

        private static void Postfix(MatchBase __instance, ref IEnumerator __result)
        {
            if (MatchSync.IsClient) __result = Wrap(__instance, __result);
        }

        private static IEnumerator Wrap(MatchBase match, IEnumerator inner)
        {
            int round = -1;
            float endedAt = -1f;
            while (true)
            {
                if (round < 0) round = match.RoundNumber;
                if (endedAt < 0f && MatchSync.Instance.ClientRoundOver(round)) endedAt = Time.realtimeSinceStartup;
                if (endedAt >= 0f && !MatchSync.Instance.HasRoundWinner(round)
                    && Time.realtimeSinceStartup - endedAt < MaxWait)
                {
                    yield return null;
                    continue;
                }
                if (!inner.MoveNext()) yield break;
                yield return inner.Current;
            }
        }
    }

    /// <summary>Online score screen: a single "Back to lobby"; the host's choice brings everyone back.</summary>
    [HarmonyPatch(typeof(MatchBase), "SetupVersusMenu")]
    internal static class VersusMenuPatch
    {
        private static bool Prefix(MatchBase __instance)
        {
            if (!MatchSync.Online) return true;
            __instance.ScoreScreen.ClearMenuOptions();
            __instance.ScoreScreen.AddMenuOption("Back to lobby");
            return false;
        }
    }

    [HarmonyPatch(typeof(ScoreScreen), nameof(ScoreScreen.Proceed))]
    internal static class ScoreScreenProceedPatch
    {
        private static void Postfix()
        {
            if (MatchSync.IsHost) MatchSync.Instance.HostOnPostMatch();
        }
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>Diagnostics: when each side starts a round's spawn sequence.</summary>
    [HarmonyLib.HarmonyPatch]
    internal static class RoundStartTracePatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("RunBeforeRound");

        private static void Prefix(WandWars.MatchBase __instance)
        {
            if (MatchSync.Online) Plugin.Trace($"Sync[{(MatchSync.IsHost ? "H" : "C")}]: round {__instance.RoundNumber + 1} begins");
        }
    }
}
