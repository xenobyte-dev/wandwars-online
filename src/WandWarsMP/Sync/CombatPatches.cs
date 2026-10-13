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
        private struct State { public int Held; public Vector2 Offset; public bool Predicted; }

        private static bool Prefix(CatchSpell __instance, CatchSpell.BallInfo ballInfo, out State __state)
        {
            __state = new State { Held = __instance.HeldBalls.Count };
            if (ballInfo?.Ball != null)
                __state.Offset = (Vector2)(ballInfo.Ball.transform.position - __instance.transform.position);
            var sync = MatchSync.Instance;
            if (sync == null || Game.Match == null) return true;
            // Host: an online player's catches come only from their claims (lag-compensated).
            if (MatchSync.IsHost && !MatchSync.ApplyingClaim && sync.IsRemoteHumanCatchSpell(__instance)) return false;
            // Client: our own swing catches immediately (claimed to the host in the postfix).
            if (MatchSync.IsClient && !BallReplay.Active && sync.IsLocalCatchSpell(__instance))
            {
                if (sync.RecentlyRejected(ballInfo?.Ball)) return false;
                __state.Predicted = true;
                return true;
            }
            return !Authority.BlockLocalBall;
        }

        private static void Postfix(CatchSpell __instance, CatchSpell.BallInfo ballInfo, State __state)
        {
            if (__instance.HeldBalls.Count <= __state.Held) return;
            if (Authority.HostSending) MatchSync.Instance.HostOnCatch(__instance, ballInfo, __state.Offset);
            else if (__state.Predicted) MatchSync.Instance.ClientOnPredictedCatch(__instance, ballInfo, __state.Offset);
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
        internal struct State { public int Health; public byte Num; public bool Skipped; }

        private static bool Prefix(GameEntity __instance, int amount, GameObject damageSource, ref bool __result, out State __state)
        {
            // Resolve the player before the hit runs: a lethal hit removes the entity from its player.
            __state = new State { Health = __instance.Health, Num = MatchSync.PlayerNum(__instance) };
            if (Authority.BlockLocal) { __result = false; __state.Skipped = true; return false; }
            if (Authority.HostSending && MatchSync.Instance.HostDeferBallHit(__instance, amount, damageSource))
            {
                __result = false;
                __state.Skipped = true;
                return false;
            }
            if (Authority.HostSending) MatchSync.DamageDepth++;
            return true;
        }

        private static void Postfix(GameEntity __instance, int amount, GameObject damageSource, bool __result, State __state)
        {
            if (__state.Skipped || !Authority.HostSending) return;
            MatchSync.DamageDepth--;
            if (__result)
                MatchSync.Instance.HostOnDamage(__instance, __state.Num, amount, damageSource, __state.Health, __instance.Health <= 0);
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

    /// <summary>
    /// Fast-forward (all humans dead, skip pressed): the host signals it so clients take the game's own
    /// short path too (otherwise they run the long round-end sequence and fall seconds behind).
    /// </summary>
    [HarmonyPatch]
    internal static class FastForwardWinnerPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => RoundMethods.All("FindFastForwardRoundWinner");

        private static bool Prefix(MatchBase __instance, ref IScoreProvider __result, out bool __state)
        {
            __state = RoundMethods.WinnerDepth++ == 0;
            if (!MatchSync.IsClient || !__state) return true;
            var d = MatchSync.Instance.TakeRoundWinner(__instance.RoundNumber);
            if (d == null) return true;
            MatchSync.ApplyScores(d);
            __result = MatchSync.ResolveWinner(d);
            return false;
        }

        private static void Postfix(MatchBase __instance, IScoreProvider __result, bool __state)
        {
            RoundMethods.WinnerDepth--;
            if (!__state || !MatchSync.IsHost) return;
            MatchSync.Instance.HostOnFastForward(__instance);
            MatchSync.Instance.HostOnRoundWinner(__instance, __result);
        }
    }

    /// <summary>Client: one synthetic press of the skip button when the host fast-forwarded.</summary>
    [HarmonyPatch(typeof(InControl.OneAxisInputControl), nameof(InControl.OneAxisInputControl.WasPressed), MethodType.Getter)]
    internal static class ForcedSkipPressPatch
    {
        /// <summary>Host fast-forwarded this round (set on receipt).</summary>
        internal static bool Pending;
        /// <summary>The game's skip prompt is up: the next Action1 check is the skip loop's.</summary>
        internal static bool Armed;

        /// <summary>True if the code reading the button is RunRound's own skip loop (not Tick's checks).</summary>
        private static bool DirectCallerIsRunRound()
        {
            var frames = new System.Diagnostics.StackTrace().GetFrames();
            if (frames == null) return false;
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null) continue;
                string n = m.Name;
                var t = m.DeclaringType;
                if (t == typeof(ForcedSkipPressPatch) || n.Contains("WasPressed") || n.Contains("Postfix")) continue;
                // First real caller: RunRound's iterator MoveNext.
                return t != null && t.Name.Contains("RunRound");
            }
            return false;
        }

        private static void Postfix(InControl.OneAxisInputControl __instance, ref bool __result)
        {
            if (!MatchSync.IsClient || Game.Match == null) return;
            if (!(__result || (Pending && Armed))) return;
            if (__instance != InControl.InputManager.ActiveDevice.Action1) return;
            // The UI input module and Tick() poll this too; only RunRound's skip loop is overridden.
            if (!DirectCallerIsRunRound()) return;
            // In the skip loop a client only ever skips when the host did.
            __result = Pending && Armed;
            if (__result)
            {
                Pending = false;
                Armed = false;
                Plugin.Trace("Sync[C]: synthetic skip press");
            }
        }
    }

    [HarmonyPatch(typeof(SkipInstructions), nameof(SkipInstructions.Show))]
    internal static class SkipPromptShownPatch
    {
        private static void Postfix()
        {
            if (MatchSync.IsClient) { ForcedSkipPressPatch.Armed = true; Plugin.Trace("Sync[C]: skip prompt shown, pending=" + ForcedSkipPressPatch.Pending); }
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
        private static readonly System.Collections.Generic.HashSet<int> tracedRound = new System.Collections.Generic.HashSet<int>();
        private static void Postfix(ref bool __result)
        {
            // Clients keep the game's own answer: they enter the skip-wait at the same moment as the
            // host, but the skip itself only happens on the host's signal (see ForcedSkipPressPatch).
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
