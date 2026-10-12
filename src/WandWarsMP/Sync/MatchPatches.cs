using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Staple;
using UnityEngine;
using WandWars;

namespace WandWarsMP.Sync
{
    [HarmonyPatch(typeof(GameLobby), nameof(GameLobby.CreateMatch))]
    internal static class CreateMatchPatch
    {
        private static void Postfix(string level, List<Player> players, MatchConfig config)
        {
            if (MatchSync.IsHost) MatchSync.Instance.HostOnMatchCreated(level, players, config);
        }
    }

    /// <summary>Client: entities never get local control. Input and AI live on the host only.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.AddEntity))]
    internal static class AddEntityPatch
    {
        private static void Postfix(GameEntity entity)
        {
            if (!MatchSync.IsClient || entity == null) return;
            var input = entity.GetComponent<PlayerInput>();
            if (input != null) Object.DestroyImmediate(input);
            StripAI(entity.gameObject);
        }

        /// <summary>Removes every AI brain (EntityAI adds CharacterAI/PacmanAI/... in its Awake).</summary>
        internal static void StripAI(GameObject go)
        {
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (c is EntityAI || n.EndsWith("AI") || n == "AIPathingAgent") Object.DestroyImmediate(c);
            }
        }
    }

    // ---- actor relay (host records; client only runs these from replay) ----

    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Move))]
    internal static class MovePatch
    {
        private static bool Prefix() => !MatchSync.IsClient || Replay.Active || Game.Match == null;
        private static void Postfix(PlayerController __instance, Vector2 direction)
        {
            if (MatchSync.IsHost) MatchSync.Instance.HostRecordMove(__instance, direction);
        }
    }

    internal static class AbilityRelay
    {
        public static bool AllowOnClient() => !MatchSync.IsClient || Replay.Active || Game.Match == null;
        public static void Record(GameEntity pc, byte ability)
        {
            if (MatchSync.IsHost) MatchSync.Instance.HostRecordAbility(pc, ability);
        }
    }

    // Prefix on host records before the ability runs, so clients replay with the same pre-cast state
    // (e.g. lastMoveDirection used by the catch dash).
    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.AbilityOne))]
    internal static class AbilityOnePatch
    {
        private static bool Prefix(PlayerController __instance)
        {
            AbilityRelay.Record(__instance, MatchSync.AbOne);
            return AbilityRelay.AllowOnClient();
        }
    }

    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.AbilityOneRelease))]
    internal static class AbilityOneReleasePatch
    {
        private static bool Prefix(PlayerController __instance)
        {
            AbilityRelay.Record(__instance, MatchSync.AbOneRelease);
            return AbilityRelay.AllowOnClient();
        }
    }

    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.AbilityTwo))]
    internal static class AbilityTwoPatch
    {
        private static bool Prefix(PlayerController __instance)
        {
            AbilityRelay.Record(__instance, MatchSync.AbTwo);
            return AbilityRelay.AllowOnClient();
        }
    }

    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.AbilityThree))]
    internal static class AbilityThreePatch
    {
        private static bool Prefix(PlayerController __instance)
        {
            AbilityRelay.Record(__instance, MatchSync.AbThree);
            return AbilityRelay.AllowOnClient();
        }
    }

    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.AbilityThreeRelease))]
    internal static class AbilityThreeReleasePatch
    {
        private static bool Prefix(PlayerController __instance)
        {
            AbilityRelay.Record(__instance, MatchSync.AbThreeRelease);
            return AbilityRelay.AllowOnClient();
        }
    }

    [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.AbilityFour))]
    internal static class AbilityFourPatch
    {
        private static bool Prefix(PlayerController __instance)
        {
            AbilityRelay.Record(__instance, MatchSync.AbFour);
            return AbilityRelay.AllowOnClient();
        }
    }

    // ---- round spawns: host picks points, everyone spawns identically ----

    /// <summary>
    /// Replaces MatchBase.Spawn (base implementation; modes that override Spawn are not synced yet).
    /// Host chooses shuffled points + ball player as the game would and broadcasts them; the client
    /// waits for that message, then both run the same spawn sequence.
    /// </summary>
    [HarmonyPatch(typeof(MatchBase), "Spawn")]
    internal static class SpawnPatch
    {
        private static bool Prefix(MatchBase __instance, Player playerWithBall, ref IEnumerator __result)
        {
            if (!MatchSync.Online) return true;
            __result = SyncedSpawn(__instance, playerWithBall);
            return false;
        }

        private static IEnumerator SyncedSpawn(MatchBase match, Player playerWithBall)
        {
            var t = Traverse.Create(match);
            var players = t.Field("players").GetValue<List<Player>>();
            var spawnPoints = match.PlayerSpawn.SpawnPoints;
            int round = match.RoundNumber;
            int[] order;

            if (MatchSync.IsHost)
            {
                order = new int[spawnPoints.Length];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                Utils.Shuffle(order);
                if (playerWithBall == null) playerWithBall = players.PickRandom();
                MatchSync.Instance.HostSendRoundSpawn(round, order, playerWithBall);
            }
            else
            {
                MatchSync.RoundSpawnData data;
                while ((data = MatchSync.Instance.TakeRoundSpawn(round)) == null) yield return null;
                order = data.Points;
                playerWithBall = data.BallPlayer == MatchSync.NoPlayer ? null
                    : players.Find(p => (byte)p.PlayerNumber == data.BallPlayer);
            }

            float first = t.Field("firstSpawnInterval").GetValue<float>();
            float interval = t.Field("spawnInterval").GetValue<float>();
            var rule = match.PlayerSpawn.BallSpawnRuleType;
            for (int i = 0; i < players.Count; i++)
            {
                bool spawnBall = rule == BallSpawnRuleType.AllGetBall;
                var point = spawnPoints[order[i % order.Length]];
                match.SpawnCharacter(players[i], spawnBall, round <= 1, point, activateImmunity: false);
                yield return new WaitForSeconds(round <= 1 ? first : interval);
            }
            if (playerWithBall != null)
                yield return match.StartCoroutine(t.Method("SpawnBall", playerWithBall).GetValue<IEnumerator>());
        }
    }
}


namespace WandWarsMP.Sync
{
    /// <summary>The Hen Hunt / Last Hen Standing beast is its own actor; relay it exactly like a wizard.</summary>
    [HarmonyPatch]
    internal static class PacmanActorPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var n in new[] { "Move", "AbilityOne", "AbilityOneRelease", "AbilityTwo", "AbilityThree", "AbilityThreeRelease", "AbilityFour" })
            {
                var m = AccessTools.DeclaredMethod(typeof(Pacman), n);
                if (m != null) yield return m;
            }
        }

        private static bool Prefix(Pacman __instance, System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            if (MatchSync.IsHost)
            {
                string n = __originalMethod.Name;
                if (n == "Move") MatchSync.Instance.HostRecordMove(__instance, (Vector2)__args[0]);
                else AbilityRelay.Record(__instance, AbilityId(n));
            }
            return AbilityRelay.AllowOnClient();
        }

        private static byte AbilityId(string n)
        {
            switch (n)
            {
                case "AbilityOne": return MatchSync.AbOne;
                case "AbilityOneRelease": return MatchSync.AbOneRelease;
                case "AbilityTwo": return MatchSync.AbTwo;
                case "AbilityThree": return MatchSync.AbThree;
                case "AbilityThreeRelease": return MatchSync.AbThreeRelease;
                default: return MatchSync.AbFour;
            }
        }
    }
}
