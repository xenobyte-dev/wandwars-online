using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WandWars;

namespace WandWarsMP.Sync
{
    /// <summary>Set while a client applies host ball-possession messages (catch/launch/release).</summary>
    internal static class BallReplay
    {
        public static bool Active;
    }

    internal static class Authority
    {
        /// <summary>Client in a match, outside a host-driven replay: local outcome logic must not run.</summary>
        public static bool BlockLocal => MatchSync.IsClient && Game.Match != null && !Replay.Active;
        public static bool BlockLocalBall => MatchSync.IsClient && Game.Match != null && !BallReplay.Active;
        public static bool HostSending => MatchSync.IsHost && Game.Match != null;
    }

    // ================= ball existence =================

    /// <summary>Client: balls exist only when the host spawns them.</summary>
    [HarmonyPatch(typeof(Ball), "OnEnable")]
    internal static class BallOnEnablePatch
    {
        private static bool Prefix(Ball __instance)
        {
            if (!MatchSync.IsClient || Game.Match == null || MatchSync.SpawningNetBall) return true;
            if (__instance.TrailParticles != null) Object.Destroy(__instance.TrailParticles.gameObject);
            __instance.gameObject.SetActive(false);
            Object.Destroy(__instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(Ball), nameof(Ball.Explode))]
    internal static class BallExplodePatch
    {
        private static bool Prefix() => !Authority.BlockLocal;
    }

    [HarmonyPatch(typeof(Ball), nameof(Ball.MergeInto))]
    internal static class BallMergePatch
    {
        private static bool Prefix() => !Authority.BlockLocal;
    }

    [HarmonyPatch(typeof(MatchBase), nameof(MatchBase.SpawnRandomBall))]
    internal static class SpawnRandomBallPatch
    {
        private static bool Prefix() => !Authority.BlockLocal;
    }

    // ================= possession =================

    [HarmonyPatch(typeof(CatchSpell), "HandleCatch")]
    internal static class HandleCatchPatch
    {
        private struct State { public int Held; public Vector2 Offset; }

        private static bool Prefix(CatchSpell __instance, CatchSpell.BallInfo ballInfo, out State __state)
        {
            __state = new State { Held = __instance.HeldBalls.Count };
            if (ballInfo?.Ball != null)
                __state.Offset = (Vector2)(ballInfo.Ball.transform.position - __instance.transform.position);
            return !Authority.BlockLocalBall;
        }

        private static void Postfix(CatchSpell __instance, CatchSpell.BallInfo ballInfo, State __state)
        {
            if (Authority.HostSending && __instance.HeldBalls.Count > __state.Held)
                MatchSync.Instance.HostOnCatch(__instance, ballInfo, __state.Offset);
        }
    }

    [HarmonyPatch(typeof(CatchSpell), "Launch")]
    internal static class LaunchPatch
    {
        private static bool Prefix(CatchSpell __instance, CatchSpell.BallInfo ballInfo, out bool __state)
        {
            __state = ballInfo != null && ballInfo.Rigidbody2D != null && __instance.HeldBalls.Contains(ballInfo);
            return !Authority.BlockLocalBall;
        }

        private static void Postfix(CatchSpell __instance, CatchSpell.BallInfo ballInfo, bool __state)
        {
            if (Authority.HostSending && __state) MatchSync.Instance.HostOnLaunch(__instance, ballInfo);
        }
    }

    [HarmonyPatch(typeof(CatchSpell), nameof(CatchSpell.ReleaseAllBalls))]
    internal static class ReleaseAllBallsPatch
    {
        private static bool Prefix(CatchSpell __instance, out List<Ball> __state)
        {
            __state = null;
            if (Authority.HostSending && __instance.HeldBalls.Count > 0)
            {
                __state = new List<Ball>();
                foreach (var info in __instance.HeldBalls)
                    if (info.Rigidbody2D != null && info.Ball != null) __state.Add(info.Ball);
            }
            return !Authority.BlockLocalBall;
        }

        private static void Postfix(CatchSpell __instance, List<Ball> __state)
        {
            if (__state != null && __state.Count > 0) MatchSync.Instance.HostOnRelease(__instance, __state);
        }
    }

    // ================= damage / death =================

    [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.TakeDamage))]
    internal static class TakeDamagePatch
    {
        private static bool Prefix(GameEntity __instance, ref bool __result, out int __state)
        {
            __state = __instance.Health;
            if (Authority.BlockLocal) { __result = false; return false; }
            if (Authority.HostSending) MatchSync.DamageDepth++;
            return true;
        }

        private static void Postfix(GameEntity __instance, int amount, GameObject damageSource, bool __result, int __state)
        {
            if (!Authority.HostSending) return;
            MatchSync.DamageDepth--;
            if (__result)
                MatchSync.Instance.HostOnDamage(__instance, amount, damageSource, __state, __instance.Health <= 0);
        }
    }

    [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.Die))]
    internal static class GameEntityDiePatch
    {
        private static bool Prefix(GameEntity __instance, GameObject killSource)
        {
            if (Authority.BlockLocal) return false;
            if (Authority.HostSending && MatchSync.DamageDepth == 0)
                MatchSync.Instance.HostOnDeath(__instance, killSource);
            return true;
        }
    }

    /// <summary>PlayerController.Die does work before calling base.Die; block the whole override too.</summary>
    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Die))]
    internal static class PlayerDiePatch
    {
        private static bool Prefix() => !Authority.BlockLocal;
    }

    // ================= hex =================

    [HarmonyPatch(typeof(HexSpell), nameof(HexSpell.TransformIntoAvatar))]
    internal static class TransformIntoAvatarPatch
    {
        private static IEnumerator Nothing() { yield break; }

        private static bool Prefix(ref IEnumerator __result)
        {
            if (!Authority.BlockLocal) return true;
            __result = Nothing();
            return false;
        }

        private static void Postfix(GameEntity entity, float duration)
        {
            if (Authority.HostSending) MatchSync.Instance.HostOnHex(entity, duration);
        }
    }

    /// <summary>Client: arcane shots never hit locally (hexes/damage arrive from the host).</summary>
    [HarmonyPatch(typeof(ArcaneShot), "OnTriggerEnter2D")]
    internal static class ArcaneShotHitPatch
    {
        private static bool Prefix() => !Authority.BlockLocal;
    }

    // ================= rounds (every mode) =================

    internal static class RoundMethods
    {
        /// <summary>The base method plus every mode's override of it, so all modes are covered.</summary>
        public static IEnumerable<System.Reflection.MethodBase> All(string name)
        {
            foreach (var t in typeof(MatchBase).Assembly.GetTypes())
            {
                if (!typeof(MatchBase).IsAssignableFrom(t)) continue;
                var m = AccessTools.DeclaredMethod(t, name);
                if (m != null && !m.IsAbstract) yield return m;
            }
        }

        /// <summary>Overrides may call base: only the outermost call talks to the network.</summary>
        public static int WinnerDepth;
    }

    [HarmonyPatch]
    internal static class FindRoundWinnerPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("FindRoundWinner");

        private static bool Prefix(MatchBase __instance, ref IScoreProvider __result, out bool __state)
        {
            __state = RoundMethods.WinnerDepth++ == 0;
            if (!MatchSync.IsClient || !__state) return true;
            var d = MatchSync.Instance.TakeRoundWinner(__instance.RoundNumber);
            if (d == null)
            {
                Plugin.Trace($"Sync[C]: no host winner for round {__instance.RoundNumber} yet; using local result");
                return true;
            }
            MatchSync.ApplyScores(d);
            __result = MatchSync.ResolveWinner(d);
            return false;
        }

        private static void Postfix(MatchBase __instance, IScoreProvider __result, bool __state)
        {
            RoundMethods.WinnerDepth--;
            if (__state && MatchSync.IsHost) MatchSync.Instance.HostOnRoundWinner(__instance, __result);
        }
    }

    /// <summary>Host's fast-forward (all humans dead, skip pressed) ends the round too.</summary>
    [HarmonyPatch]
    internal static class FastForwardWinnerPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("FindFastForwardRoundWinner");

        private static void Prefix(out bool __state) => __state = RoundMethods.WinnerDepth++ == 0;

        private static void Postfix(MatchBase __instance, IScoreProvider __result, bool __state)
        {
            RoundMethods.WinnerDepth--;
            if (!__state || !MatchSync.IsHost) return;
            MatchSync.Instance.HostOnRoundOver(__instance);
            MatchSync.Instance.HostOnRoundWinner(__instance, __result);
        }
    }

    [HarmonyPatch]
    internal static class EndRoundConditionPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("EndRoundCondition");

        private static void Postfix(MatchBase __instance, ref bool __result)
        {
            if (MatchSync.IsHost && __result) MatchSync.Instance.HostOnRoundOver(__instance);
            // Client: only the host ends rounds (local timers/conditions can run ahead of the host).
            else if (MatchSync.IsClient) __result = MatchSync.Instance.ClientRoundOver(__instance.RoundNumber);
        }
    }

    // ================= Merlinball goals =================

    /// <summary>Goals are decided on the host; clients replay the target's own trigger (visuals + scoring).</summary>
    [HarmonyPatch(typeof(MerlinBallTarget), "OnTriggerEnter2D")]
    internal static class MerlinGoalPatch
    {
        /// <summary>Host: the ball/target being processed, so OnBallInTarget knows what scored.</summary>
        internal static Ball PendingBall;
        internal static MerlinBallTarget PendingTarget;

        private static bool Prefix(MerlinBallTarget __instance, Collider2D col)
        {
            if (Authority.BlockLocal) return false;
            if (Authority.HostSending && col != null)
            {
                PendingBall = col.GetComponent<Ball>();
                PendingTarget = __instance;
            }
            return true;
        }

        private static void Postfix()
        {
            PendingBall = null;
            PendingTarget = null;
        }
    }

    /// <summary>Host: the mode accepted a goal (fires from inside the target trigger).</summary>
    [HarmonyPatch(typeof(MerlinBall), "OnBallInTarget")]
    internal static class MerlinBallInTargetPatch
    {
        private static void Prefix()
        {
            var ball = MerlinGoalPatch.PendingBall;
            if (Authority.HostSending && ball != null && MerlinGoalPatch.PendingTarget != null)
                MatchSync.Instance.HostOnGoal(MerlinGoalPatch.PendingTarget, ball);
        }
    }

    /// <summary>Client: never skip rounds locally (the host's RoundOver ends them).</summary>
    [HarmonyPatch(typeof(MatchTypeExtensions), nameof(MatchTypeExtensions.IsSkippable))]
    internal static class IsSkippablePatch
    {
        private static void Postfix(ref bool __result)
        {
            if (MatchSync.IsClient && Game.Match != null) __result = false;
        }
    }

    /// <summary>Online: the pause menu freezes the whole simulation for one machine only. Disabled.</summary>
    [HarmonyPatch(typeof(MatchBase), nameof(MatchBase.ToggleFightMenu))]
    internal static class PauseMenuPatch
    {
        private static bool Prefix() => !MatchSync.Online;
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Client: its round cleanup runs a moment after the host's (it waits for the host's winner), by
    /// which time the host may already have spawned the next round's ball/items. Host-owned objects are
    /// removed only by the host's despawn messages.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(WandWars.Ball), nameof(WandWars.Ball.OnLevelReset))]
    internal static class BallLevelResetPatch
    {
        private static bool Prefix() => !(MatchSync.IsClient && WandWars.Game.Match != null);
    }

    [HarmonyLib.HarmonyPatch(typeof(WandWars.Ball), nameof(WandWars.Ball.CleanUpAllBalls))]
    internal static class BallCleanUpPatch
    {
        private static bool Prefix() => !(MatchSync.IsClient && WandWars.Game.Match != null);
    }

    [HarmonyLib.HarmonyPatch(typeof(WandWars.PowerupItem), nameof(WandWars.PowerupItem.DestroyAll))]
    internal static class ItemsDestroyAllPatch
    {
        private static bool Prefix() => !(MatchSync.IsClient && WandWars.Game.Match != null);
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Host: an arcane shot that hit a player dies; tell clients so their (non-colliding) copy of that
    /// caster's shot dies at the same spot instead of flying through the target.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(WandWars.ArcaneShot), "OnTriggerEnter2D")]
    internal static class ArcaneShotHitHostPatch
    {
        private static void Prefix(WandWars.ArcaneShot __instance, out WandWars.ArcaneShot.ArcaneShotState __state) => __state = __instance.State;

        private static void Postfix(WandWars.ArcaneShot __instance, WandWars.ArcaneShot.ArcaneShotState __state)
        {
            if (Authority.HostSending && __state != WandWars.ArcaneShot.ArcaneShotState.Dead
                && __instance.State == WandWars.ArcaneShot.ArcaneShotState.Dead)
                MatchSync.Instance.HostOnShotDead(__instance);
        }
    }
}
