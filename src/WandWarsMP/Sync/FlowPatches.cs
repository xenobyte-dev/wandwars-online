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
            bool released = false;
            while (true)
            {
                if (round < 0) round = match.RoundNumber;
                if (endedAt < 0f && MatchSync.Instance.ClientRoundOver(round)) endedAt = Time.realtimeSinceStartup;
                if (!released && endedAt >= 0f)
                {
                    // Hold once, until the host's winner is here (it gets consumed right after).
                    if (!MatchSync.Instance.HasRoundWinner(round) && Time.realtimeSinceStartup - endedAt < MaxWait)
                    {
                        yield return null;
                        continue;
                    }
                    released = true;
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

namespace WandWarsMP.Sync
{
    /// <summary>
    /// The winner's taunt only plays (and waits ~4s) if the winner's entity is an active wizard, which can
    /// differ between host and client (beast vs wizard). The host signals when its taunt is over; clients
    /// still play theirs visually but wait on the host's signal instead of their own timer.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(WandWars.MatchBase), "DoWinnerTaunt")]
    internal static class WinnerTauntPatch
    {
        private const float MaxWait = 8f;

        private static void Postfix(WandWars.MatchBase __instance, ref System.Collections.IEnumerator __result)
        {
            if (MatchSync.IsHost) __result = HostWrap(__instance, __result);
            else if (MatchSync.IsClient) __result = ClientWait(__instance, __result);
        }

        /// <summary>Host: currently stepping the winner-taunt coroutine (only those taunts are broadcast).</summary>
        internal static bool InWinnerTaunt;

        private static System.Collections.IEnumerator HostWrap(WandWars.MatchBase match, System.Collections.IEnumerator inner)
        {
            while (true)
            {
                bool more;
                InWinnerTaunt = true;
                try { more = inner.MoveNext(); }
                finally { InWinnerTaunt = false; }
                if (!more) break;
                yield return inner.Current;
            }
            MatchSync.Instance.HostOnTauntDone(match.RoundNumber);
        }

        private static System.Collections.IEnumerator ClientWait(WandWars.MatchBase match, System.Collections.IEnumerator inner)
        {
            // The local taunt is skipped: the host tells us who actually taunts (TauntPlay), because a
            // taunt freezes game time and must only happen if it happened on the host.
            int round = match.RoundNumber;
            float end = UnityEngine.Time.realtimeSinceStartup + MaxWait;
            float start = UnityEngine.Time.realtimeSinceStartup;
            while (!MatchSync.Instance.TauntDone(round) && UnityEngine.Time.realtimeSinceStartup < end) yield return null;
            Plugin.Trace($"Sync[C]: taunt wait round {round} took {UnityEngine.Time.realtimeSinceStartup - start:F2}s (done={MatchSync.Instance.TauntDone(round)})");
        }
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>Diagnostics: timestamps of the post-round steps on both sides.</summary>
    [HarmonyLib.HarmonyPatch]
    internal static class PostRoundTracePatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var n in new[] { "DoWinnerTaunt", "ShowRoundScore", "DoRoundCleanup", "ProcessStatistics", "EndMatch" })
                foreach (var m in RoundMethods.All(n)) yield return m;
        }

        private static void Prefix(System.Reflection.MethodBase __originalMethod)
        {
            if (MatchSync.Online && Dev.DevMode.Enabled)
                Plugin.Trace($"Sync[{(MatchSync.IsHost ? "H" : "C")}]: step {__originalMethod.DeclaringType.Name}.{__originalMethod.Name} ts={UnityEngine.Time.timeScale}");
        }
    }
}


namespace WandWarsMP.Sync
{
    /// <summary>Host: a player taunts (post-round); clients play the same taunt.</summary>
    [HarmonyLib.HarmonyPatch(typeof(WandWars.PlayerController), nameof(WandWars.PlayerController.Taunt))]
    internal static class TauntPatch
    {
        private static bool Prefix(WandWars.PlayerController __instance)
        {
            if (MatchSync.IsHost && WinnerTauntPatch.InWinnerTaunt) MatchSync.Instance.HostOnTaunt(__instance);
            return !(MatchSync.IsClient && WandWars.Game.Match != null && !Replay.Active);
        }
    }

    /// <summary>Client: no own round-end grace; the hold for the host's winner already covers it.</summary>
    [HarmonyLib.HarmonyPatch]
    internal static class ClientGracePatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("Start");

        private static void Postfix(WandWars.MatchBase __instance)
        {
            if (MatchSync.IsClient) HarmonyLib.Traverse.Create(__instance).Field("roundWinGracePeriod").SetValue(0f);
        }
    }
}


namespace WandWarsMP.Sync
{
    [HarmonyLib.HarmonyPatch(typeof(WandWars.Player), nameof(WandWars.Player.DespawnEntities))]
    internal static class DespawnTracePatch
    {
        private static void Prefix(WandWars.Player __instance)
        {
            if (MatchSync.Online && Dev.DevMode.Enabled && __instance.PlayerNumber == WandWars.PlayerNumber.One)
            {
                bool paused = global::SingletonBehaviour<Staple.GameTime>.Instance.Paused;
                Plugin.Trace($"Sync[{(MatchSync.IsHost ? "H" : "C")}]: despawn ts={UnityEngine.Time.timeScale} paused={paused}");
            }
        }
    }
}
