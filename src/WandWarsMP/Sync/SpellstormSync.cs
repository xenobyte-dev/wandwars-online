using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Staple;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Spellstorm (co-op boss fights). Bosses are generated identically on both sides (same seed) and
    /// bound to a host id; their moves/abilities/special casts are relayed by id; summoned minions are
    /// host-spawned NetEntities; portal entries and the "press a button" speeches are host-driven.
    /// </summary>
    public sealed partial class MatchSync
    {
        public const byte ActSpecial = 1, ActShield = 2, ActTeleport = 3;

        internal GameEntity EntityById(ushort id) =>
            netEntities.TryGetValue(id, out var ne) && ne != null ? ne.GetComponent<GameEntity>() : null;

        // ---- boss binding ----

        private readonly Dictionary<int, GameEntity> localBosses = new Dictionary<int, GameEntity>();
        private readonly Dictionary<int, ushort> pendingBossBinds = new Dictionary<int, ushort>();

        internal void HostBindBoss(GameEntity boss, int index)
        {
            var ne = boss.gameObject.AddComponent<NetEntity>();
            ne.Id = nextEntityId++;
            ne.Prefab = PrefabName(boss.gameObject);
            netEntities[ne.Id] = ne;
            var w = net.Begin(Msg.BossBind);
            w.U16(ne.Id);
            w.U8((byte)index);
            net.Broadcast(w, true);
            Plugin.Trace($"Sync[H]: boss {index} is entity {ne.Id} ({ne.Prefab}, health {boss.Health})");
        }

        internal void ClientLocalBoss(GameEntity boss, int index)
        {
            AddEntityPatch.StripAI(boss.gameObject); // after the game configured it (passives write into the AI)
            localBosses[index] = boss;
            TryBindBoss(index);
        }

        private void OnBossBind(ref NetReader r)
        {
            ushort id = r.U16();
            int index = r.U8();
            pendingBossBinds[index] = id;
            TryBindBoss(index);
        }

        private void TryBindBoss(int index)
        {
            if (!localBosses.TryGetValue(index, out var boss) || boss == null) return;
            if (!pendingBossBinds.TryGetValue(index, out var id)) return;
            pendingBossBinds.Remove(index);
            var ne = boss.GetComponent<NetEntity>() ?? boss.gameObject.AddComponent<NetEntity>();
            ne.Id = id;
            ne.Prefab = PrefabName(boss.gameObject);
            netEntities[id] = ne;
            Plugin.Trace($"Sync[C]: boss {index} bound as entity {id} ({ne.Prefab}, health {boss.Health})");
        }

        // ---- entity abilities / special actions ----

        private void OnEntityAbility(ref NetReader r)
        {
            var ent = EntityById(r.U16());
            byte ability = r.U8();
            var look = new Vector2(r.I8() / 127f, r.I8() / 127f);
            var actor = ent as ICharacterActor;
            if (actor == null || !ent.isActiveAndEnabled) return;
            using (Replay.Begin())
            {
                if (look != Vector2.zero) Traverse.Create(ent).Field("lastMoveDirection").SetValue(look.normalized);
                switch (ability)
                {
                    case AbOne: actor.AbilityOne(); break;
                    case AbOneRelease: actor.AbilityOneRelease(); break;
                    case AbTwo: actor.AbilityTwo(); break;
                    case AbThree: actor.AbilityThree(); break;
                    case AbThreeRelease: actor.AbilityThreeRelease(); break;
                    case AbFour: actor.AbilityFour(); break;
                }
            }
        }

        internal void HostOnEntityAction(GameEntity e, byte action, Vector2 arg)
        {
            var ne = e.GetComponent<NetEntity>();
            if (ne == null) return;
            var w = net.Begin(Msg.EntityAction);
            w.U16(ne.Id);
            w.U8(action);
            w.Vec2(arg);
            net.Broadcast(w, true);
        }

        private void OnEntityAction(ref NetReader r)
        {
            var knight = EntityById(r.U16()) as Knight;
            byte action = r.U8();
            Vector2 arg = r.Vec2();
            if (knight == null || !knight.isActiveAndEnabled) return;
            using (Replay.Begin())
            {
                switch (action)
                {
                    case ActSpecial: knight.CastSpecialSpell(); break;
                    case ActShield: knight.CastShieldSpell(); break;
                    case ActTeleport: knight.Teleport((Vector3)arg); break;
                }
            }
        }

        // ---- barriers (host-paced sequences) ----

        private readonly HashSet<int> barriers = new HashSet<int>();
        internal bool BarrierReached(int code) => barriers.Contains(code);

        internal void HostBarrier(int code)
        {
            var w = net.Begin(Msg.Barrier);
            w.I32(code);
            net.Broadcast(w, true);
        }

        // ---- portal ----

        internal void HostOnPortalEnter(PlayerController pc)
        {
            byte num = PlayerNum(pc);
            if (num == NoPlayer) return;
            var w = net.Begin(Msg.PortalEnter);
            w.U8(num);
            net.Broadcast(w, true);
        }

        private void OnPortalEnter(ref NetReader r)
        {
            var pc = EntityOf(r.U8());
            var portal = Object.FindObjectOfType<TrialPortal>();
            if (pc == null || portal == null) return;
            var t = Traverse.Create(portal);
            var processed = t.Field("processedPlayerObjects").GetValue<List<GameObject>>();
            if (processed != null && processed.Contains(pc.gameObject)) return;
            processed?.Add(pc.gameObject);
            pc.Player.EnteredPortal = true;
            t.Field("teleportedEntities").SetValue(t.Field("teleportedEntities").GetValue<int>() + 1);
            using (Replay.Begin())
                portal.StartCoroutine(t.Method("TeleOut", pc).GetValue<IEnumerator>());
        }

        // ---- stages ----

        internal static string PendingSpellstormStage;

        internal void HostOnSpellstormStage(string stage)
        {
            var w = net.Begin(Msg.SpellstormStage);
            w.Str(stage);
            net.Broadcast(w, true);
        }

        private void ResetSpellstorm()
        {
            localBosses.Clear();
            pendingBossBinds.Clear();
            barriers.Clear();
        }

        private void OnSpellstormMessage(Msg msg, ref NetReader r)
        {
            switch (msg)
            {
                case Msg.BossBind: OnBossBind(ref r); break;
                case Msg.EntityAbility: OnEntityAbility(ref r); break;
                case Msg.EntityAction: OnEntityAction(ref r); break;
                case Msg.Barrier: barriers.Add(r.I32()); break;
                case Msg.PortalEnter: OnPortalEnter(ref r); break;
                case Msg.SpellstormStage: PendingSpellstormStage = r.Str(); break;
            }
        }
    }

    // ================= patches =================

    /// <summary>Same random boss (prefab, spawn point, health, passives, name) on every machine.</summary>
    [HarmonyPatch(typeof(BossFight), "SpawnBoss")]
    internal static class SpawnBossPatch
    {
        private static void Prefix(int index)
        {
            if (MatchSync.Online) Random.seed = MatchSync.Instance.CurrentSeed ^ ((index + 1) * 7919);
        }

        private static void Postfix(BossFight.Boss boss, int index)
        {
            if (boss?.Entity == null) return;
            if (MatchSync.IsHost) MatchSync.Instance.HostBindBoss(boss.Entity, index);
            else if (MatchSync.IsClient) MatchSync.Instance.ClientLocalBoss(boss.Entity, index);
        }
    }

    /// <summary>Same final boss and turret layout on every machine.</summary>
    [HarmonyPatch]
    internal static class SeededSetupPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Spellstorm), nameof(Spellstorm.Start));
            yield return AccessTools.Method(typeof(TurretSpawner), "SpawnTurrets");
        }

        private static void Prefix()
        {
            if (MatchSync.Online) Random.seed = MatchSync.Instance.CurrentSeed;
        }
    }

    /// <summary>Knight special casts: host relays, clients replay (summoning comes as minion spawns).</summary>
    [HarmonyPatch]
    internal static class KnightActionPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Knight), nameof(Knight.CastSpecialSpell));
            yield return AccessTools.Method(typeof(Knight), nameof(Knight.CastShieldSpell));
            yield return AccessTools.Method(typeof(Knight), nameof(Knight.CastSummoningSpell));
        }

        private static bool Prefix(System.Reflection.MethodBase __originalMethod, ref bool __result)
        {
            if (Authority.BlockLocal) { __result = false; return false; }
            return true;
        }

        private static void Postfix(Knight __instance, System.Reflection.MethodBase __originalMethod, bool __runOriginal)
        {
            if (!__runOriginal || !Authority.HostSending) return;
            switch (__originalMethod.Name)
            {
                case nameof(Knight.CastSpecialSpell): MatchSync.Instance.HostOnEntityAction(__instance, MatchSync.ActSpecial, Vector2.zero); break;
                case nameof(Knight.CastShieldSpell): MatchSync.Instance.HostOnEntityAction(__instance, MatchSync.ActShield, Vector2.zero); break;
            }
        }
    }

    [HarmonyPatch(typeof(Knight), nameof(Knight.Teleport), new[] { typeof(Vector3) })]
    internal static class KnightTeleportPatch
    {
        private static bool Prefix() => !Authority.BlockLocal;

        private static void Postfix(Knight __instance, Vector3 position, bool __runOriginal)
        {
            if (__runOriginal && Authority.HostSending) MatchSync.Instance.HostOnEntityAction(__instance, MatchSync.ActTeleport, position);
        }
    }

    /// <summary>Minions: the host's summon registers the new entity (with its master) for clients.</summary>
    [HarmonyPatch(typeof(SummoningSpell), nameof(SummoningSpell.Cast))]
    internal static class SummonPatch
    {
        private static bool Prefix() => !Authority.BlockLocal;

        private static void Postfix(SummoningSpell __instance, bool __runOriginal)
        {
            if (!__runOriginal || !Authority.HostSending) return;
            foreach (var m in Object.FindObjectsOfType<SummonedMinion>())
                if (m.GetComponent<NetEntity>() == null)
                    MatchSync.Instance.HostRegisterEntity(m.gameObject, null, __instance.Caster);
        }
    }

    /// <summary>Clients never block on a local button for host-paced speeches.</summary>
    [HarmonyPatch(typeof(SpeechBubble), nameof(SpeechBubble.SayAndWaitForButton))]
    internal static class SpeechWaitPatch
    {
        private static bool Prefix(SpeechBubble __instance, string value, ref Coroutine __result)
        {
            if (!MatchSync.IsClient || Game.Match == null) return true;
            __result = __instance.SayAndWait(value);
            return false;
        }
    }

    /// <summary>Boss intro and the victory sequence are paced by the host (they wait for a human press).</summary>
    [HarmonyPatch]
    internal static class BarrierSequencePatch
    {
        private const int IntroBase = 1000, WinCode = 2000;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BossFight), nameof(BossFight.IntroductionSequence));
            yield return AccessTools.Method(typeof(Spellstorm), "WinSequence");
        }

        private static void Postfix(object __instance, System.Reflection.MethodBase __originalMethod, ref IEnumerator __result)
        {
            if (!MatchSync.Online) return;
            int code = __originalMethod.Name == "WinSequence" ? WinCode
                : IntroBase + ((BossFight)__instance).BossIndex;
            __result = Wrap(__result, code);
        }

        private static IEnumerator Wrap(IEnumerator inner, int code)
        {
            while (inner.MoveNext()) yield return inner.Current;
            if (MatchSync.IsHost) MatchSync.Instance.HostBarrier(code);
            else
            {
                float end = Time.realtimeSinceStartup + 120f;
                while (!MatchSync.Instance.BarrierReached(code) && Time.realtimeSinceStartup < end) yield return null;
            }
        }
    }

    /// <summary>Portal: entries are decided by the host.</summary>
    [HarmonyPatch(typeof(TrialPortal), "OnTriggerStay2D")]
    internal static class PortalPatch
    {
        private static bool Prefix(TrialPortal __instance, Collider2D col, out bool __state)
        {
            __state = false;
            if (Authority.BlockLocal) return false;
            var pc = col != null ? col.GetComponent<PlayerController>() : null;
            __state = pc != null && pc.Player != null && !pc.Player.EnteredPortal;
            return true;
        }

        private static void Postfix(Collider2D col, bool __state)
        {
            if (!__state || !Authority.HostSending) return;
            var pc = col.GetComponent<PlayerController>();
            if (pc != null && pc.Player != null && pc.Player.EnteredPortal) MatchSync.Instance.HostOnPortalEnter(pc);
        }
    }

    /// <summary>Next Spellstorm stage: the host's pick.</summary>
    [HarmonyPatch(typeof(Spellstorm), "GetRandomSpellstormStage")]
    internal static class SpellstormStagePatch
    {
        private static bool Prefix(ref string __result)
        {
            if (!MatchSync.IsClient || MatchSync.PendingSpellstormStage == null) return true;
            __result = MatchSync.PendingSpellstormStage;
            MatchSync.PendingSpellstormStage = null;
            return false;
        }

        private static void Postfix(string __result)
        {
            if (MatchSync.IsHost && Game.Match != null) MatchSync.Instance.HostOnSpellstormStage(__result);
        }
    }

    /// <summary>Online failure screen: host ends it for everyone, clients can leave.</summary>
    [HarmonyPatch(typeof(Spellstorm), "HandleFailure")]
    internal static class SpellstormFailurePatch
    {
        private static bool Prefix(Spellstorm __instance, ref IEnumerator __result)
        {
            if (!MatchSync.Online) return true;
            __result = Online(__instance);
            return false;
        }

        private static IEnumerator Online(Spellstorm match)
        {
            Traverse.Create(match).Field("menuEnabled").SetValue(false);
            var bf = Traverse.Create(match).Field("bossFight").GetValue<BossFight>();
            if (bf != null) yield return bf.SmackTalkAndLeave();
            var resources = Game.SpellstormResources;
            var screen = Scene.InstantiateInScene(resources.LossDialog) as GameObject;
            var dialog = screen.GetComponentInChildren<Dialog>();
            dialog.CleanUp();
            if (MatchSync.IsHost)
                dialog.AddOption("Menu", () => { MatchSync.Instance.HostAbortMatch(); match.QuitToMainMenu(); });
            else
                dialog.AddOption("Leave", () => { NetSession.Instance?.Stop(); match.QuitToMainMenu(); });
            dialog.Toggle(value: true);
        }
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Client: the boss intro aims the boss at the first human's starting spawn point, which may not be
    /// set yet (spawns are host-driven and can land a moment later). Give everyone a fallback point.
    /// </summary>
    [HarmonyPatch(typeof(BossFight), nameof(BossFight.IntroductionSequence))]
    internal static class IntroSpawnPointPatch
    {
        private static void Prefix()
        {
            var m = Game.Match;
            if (!MatchSync.IsClient || m == null || m.PlayerSpawn == null || m.PlayerSpawn.SpawnPoints.Length == 0) return;
            foreach (var p in m.Players)
                if (p.StartingSpawnPoint == null) p.StartingSpawnPoint = m.PlayerSpawn.SpawnPoints[0];
        }
    }

    /// <summary>The inventory HUD throws if an item expires that it never displayed (replay ordering).</summary>
    [HarmonyPatch(typeof(EntityInventoryDisplay), "ItemRemoved")]
    internal static class InventoryDisplayGuardPatch
    {
        private static System.Exception Finalizer(System.Exception __exception) =>
            __exception is KeyNotFoundException ? null : __exception;
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>Client: carried-over items are re-created by the host and arrive as item spawn+pickup.</summary>
    [HarmonyPatch(typeof(CarryOnPerk), nameof(CarryOnPerk.InstantiateLoadout))]
    internal static class LoadoutPatch
    {
        private static bool Prefix() => !(MatchSync.IsClient && Game.Match != null);
    }
}
