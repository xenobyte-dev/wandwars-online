using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>Network identity of a non-player GameEntity spawned during a match (chickens, minions, bosses).</summary>
    public sealed class NetEntity : MonoBehaviour
    {
        public ushort Id;
        public string Prefab;
    }

    /// <summary>
    /// Generic non-player entities: the host registers them at spawn choke points and announces
    /// (id, prefab name, position); clients instantiate the same prefab with AI stripped. Positions ride
    /// in snapshots; deaths are host-decided and replayed by id; vanished entities are despawned.
    /// </summary>
    public sealed partial class MatchSync
    {
        private readonly Dictionary<ushort, NetEntity> netEntities = new Dictionary<ushort, NetEntity>();
        private readonly List<ushort> entityScratch = new List<ushort>();
        private readonly NetWriter entityWriter = new NetWriter();
        private ushort nextEntityId = 1;
        internal static bool SpawningNetEntity;

        public int NetEntityCount => netEntities.Count(kv => kv.Value != null);

        private void ResetEntities()
        {
            netEntities.Clear();
            nextEntityId = 1;
        }

        internal static string PrefabName(GameObject go) => go.name.Replace("(Clone)", "").Trim();

        // ---- host ----

        internal ushort HostRegisterEntity(GameObject go, string prefabName = null, GameEntity master = null)
        {
            if (go == null) return 0;
            var ne = go.GetComponent<NetEntity>();
            if (ne != null) return ne.Id;
            ne = go.AddComponent<NetEntity>();
            ne.Id = nextEntityId++;
            if (nextEntityId == 0) nextEntityId = 1;
            ne.Prefab = prefabName ?? PrefabName(go);
            netEntities[ne.Id] = ne;
            var w = entityWriter;
            w.Reset();
            w.U8((byte)Msg.EntitySpawn);
            w.U16(ne.Id);
            w.Str(ne.Prefab);
            w.Vec2(go.transform.position);
            var masterNe = master != null ? master.GetComponent<NetEntity>() : null;
            w.U16(masterNe != null ? masterNe.Id : (ushort)0);
            net.Broadcast(w, true);
            return ne.Id;
        }

        private void HostTrackEntities()
        {
            entityScratch.Clear();
            foreach (var kv in netEntities)
                if (kv.Value == null) entityScratch.Add(kv.Key);
            foreach (var id in entityScratch)
            {
                netEntities.Remove(id);
                var w = net.Begin(Msg.EntityDespawn);
                w.U16(id);
                net.Broadcast(w, true);
            }
        }

        private void WriteEntitySnapshot(NetWriter w)
        {
            int count = 0;
            foreach (var kv in netEntities) if (kv.Value != null && kv.Value.isActiveAndEnabled) count++;
            w.U8((byte)Mathf.Min(count, 255));
            int n = 0;
            foreach (var kv in netEntities)
            {
                var e = kv.Value;
                if (e == null || !e.isActiveAndEnabled) continue;
                if (n++ >= 255) break;
                var rb = e.GetComponent<Rigidbody2D>();
                w.U16(kv.Key);
                w.Vec2(rb != null ? rb.position : (Vector2)e.transform.position);
                w.Vec2(rb != null ? rb.velocity : Vector2.zero);
                var ge = e.GetComponent<GameEntity>();
                w.I16((short)(ge != null ? ge.Health : 0));
            }
        }

        internal void HostOnEntityDeath(GameEntity e, GameObject src)
        {
            var ne = e.GetComponent<NetEntity>();
            if (ne == null) return;
            var w = net.Begin(Msg.EntityDeath);
            w.U16(ne.Id);
            WriteSource(w, src);
            net.Broadcast(w, true);
        }

        // ---- client ----

        private void ReadEntitySnapshot(ref NetReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                ushort id = r.U16();
                Vector2 pos = r.Vec2(), vel = r.Vec2();
                int health = r.I16();
                if (!netEntities.TryGetValue(id, out var ne) || ne == null || !ne.isActiveAndEnabled) continue;
                var ge = ne.GetComponent<GameEntity>();
                if (ge != null) ge.Health = health;
                var rb = ne.GetComponent<Rigidbody2D>();
                if (rb == null) { ne.transform.position = pos; continue; }
                Vector2 err = pos - rb.position;
                if (err.magnitude > SnapDistance) rb.position = pos; else rb.position += err * PosBlend;
                rb.velocity = vel;
            }
        }

        private static readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();

        /// <summary>Finds a loaded prefab asset by name (spawner lists first, then any loaded asset).</summary>
        internal static GameObject FindPrefab(string name)
        {
            if (prefabCache.TryGetValue(name, out var cached) && cached != null) return cached;
            GameObject found = null;
            var spawner = SingletonBehaviour<TimedPrefabSpawner>.Instance;
            if (spawner != null && spawner.Prefabs != null)
                found = spawner.Prefabs.FirstOrDefault(p => p != null && p.name == name);
            if (found == null)
                found = Resources.FindObjectsOfTypeAll<GameObject>()
                    .FirstOrDefault(g => g.name == name && !g.scene.IsValid() && g.transform.parent == null);
            if (found != null) prefabCache[name] = found;
            return found;
        }

        private void OnEntitySpawn(ref NetReader r)
        {
            ushort id = r.U16();
            string prefabName = r.Str();
            Vector2 pos = r.Vec2();
            ushort masterId = r.U16();
            if (netEntities.ContainsKey(id)) return;
            var prefab = FindPrefab(prefabName);
            if (prefab == null) { Plugin.Trace("Sync[C]: unknown entity prefab " + prefabName); return; }
            GameObject go;
            SpawningNetEntity = true;
            try { go = Staple.Scene.InstantiateInScene(prefab, pos, Quaternion.identity) as GameObject; }
            finally { SpawningNetEntity = false; }
            if (go == null) return;
            AddEntityPatch.StripAI(go);
            var ne = go.AddComponent<NetEntity>();
            ne.Id = id;
            ne.Prefab = prefabName;
            netEntities[id] = ne;
            var master = masterId != 0 ? EntityById(masterId) : null;
            var entity = go.GetComponent<GameEntity>();
            if (master != null && entity != null)
            {
                // Summoned minion: same allegiance as on the host.
                entity.BecomeAMinionOf(master);
                go.AddComponent<SummonedMinion>();
                if (entity.Inventory != null) entity.Inventory.Disabled = true;
            }
            var spawner = SingletonBehaviour<TimedPrefabSpawner>.Instance;
            if (spawner != null && spawner.SpawnEffectPrefab != null && spawner.SpawnEffectPrefab.Length > 0)
                Staple.Scene.InstantiateInScene(spawner.SpawnEffectPrefab[0], go.transform.position, Quaternion.identity);
        }

        private void OnEntityDespawn(ref NetReader r)
        {
            ushort id = r.U16();
            if (!netEntities.TryGetValue(id, out var ne)) return;
            netEntities.Remove(id);
            if (ne != null) Object.Destroy(ne.gameObject);
        }

        private void OnEntityDeath(ref NetReader r)
        {
            ushort id = r.U16();
            netEntities.TryGetValue(id, out var ne);
            var e = ne != null ? ne.GetComponent<GameEntity>() : null;
            if (e == null) { r.U8(); r.U16(); return; }
            var src = ReadSource(ref r, e);
            using (Replay.Begin()) e.Die(src);
        }
    }

    // ================= choke points =================

    /// <summary>Chickens (timed spawns and beast drops). Clients never spawn their own.</summary>
    [HarmonyPatch(typeof(TimedPrefabSpawner), nameof(TimedPrefabSpawner.Spawn), new[] { typeof(Vector3) })]
    internal static class TimedSpawnPatch
    {
        private static void Postfix(GameObject __result)
        {
            if (__result == null) return;
            if (MatchSync.IsHost && Game.Match != null) MatchSync.Instance.HostRegisterEntity(__result);
            else if (MatchSync.IsClient && Game.Match != null && !MatchSync.SpawningNetEntity)
            {
                // Callers use the returned object, so let it exist for this frame but never live.
                __result.SetActive(false);
                Object.Destroy(__result);
            }
        }
    }

    [HarmonyPatch(typeof(TimedPrefabSpawner), "Update")]
    internal static class TimedSpawnerUpdatePatch
    {
        private static bool Prefix() => !(MatchSync.IsClient && Game.Match != null);
    }

    /// <summary>Host: deaths of networked non-player entities.</summary>
    [HarmonyPatch(typeof(GameEntity), nameof(GameEntity.Die))]
    internal static class EntityDiePatch
    {
        private static void Prefix(GameEntity __instance, GameObject killSource)
        {
            if (Authority.HostSending && __instance.GetComponent<NetEntity>() != null)
                MatchSync.Instance.HostOnEntityDeath(__instance, killSource);
        }
    }

    // ================= Last Hen Standing: host picks the beast =================

    [HarmonyPatch(typeof(LastHenStanding), "TransformRandomPlayer")]
    internal static class LhsTransformPatch
    {
        private static bool Prefix() => !MatchSync.IsClient || Replay.Active;

        private static void Postfix(LastHenStanding __instance)
        {
            if (!MatchSync.IsHost) return;
            var p = Traverse.Create(__instance).Field("lastTransformedPlayer").GetValue<Player>();
            if (p != null) MatchSync.Instance.HostOnBeast(p);
        }
    }

    public sealed partial class MatchSync
    {
        internal void HostOnBeast(Player p)
        {
            var w = net.Begin(Msg.Beast);
            w.U8((byte)p.PlayerNumber);
            net.Broadcast(w, true);
        }

        private void OnBeast(ref NetReader r)
        {
            var player = PlayerOf(r.U8());
            var lhs = Game.Match as LastHenStanding;
            if (player == null || lhs == null) return;
            var t = Traverse.Create(lhs);
            t.Field("lastTransformedPlayer").SetValue(player);
            // The hens' hexes arrive as regular Hex messages; only the beast transformation is replayed here.
            using (Replay.Begin())
                if (player.PrimaryEntity != null)
                    t.Method("TransformIntoMonster", new[] { typeof(GameEntity), typeof(bool) }).GetValue(player.PrimaryEntity, false);
        }
    }
}

namespace WandWarsMP.Sync
{
    /// <summary>
    /// The egg modes' referee says a random line before each round; its length sets spawn timing.
    /// Clients say the host's line instead of rolling their own.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(WandWars.SpeechBubble), nameof(WandWars.SpeechBubble.Say))]
    internal static class RefereeLinePatch
    {
        internal static string PendingLine;

        private static bool IsRefereeLine(string v)
        {
            var g = WandWars.Game.GlobalResources;
            return v != null && g != null &&
                   (System.Array.IndexOf(g.RefereeTextHenHunt, v) >= 0 ||
                    System.Array.IndexOf(g.RefereeTextKingOfTheCoop, v) >= 0 ||
                    System.Array.IndexOf(g.RefereeTextLastHenStanding, v) >= 0);
        }

        private static void Prefix(ref string value)
        {
            if (!MatchSync.IsClient || !IsRefereeLine(value)) return;
            if (PendingLine != null) { value = PendingLine; PendingLine = null; }
        }

        private static void Postfix(string value)
        {
            if (MatchSync.IsHost && WandWars.Game.Match != null && IsRefereeLine(value))
            {
                var net = Net.NetSession.Instance;
                var w = net.Begin(Net.Msg.RefereeLine);
                w.Str(value);
                net.Broadcast(w, true);
            }
        }
    }
}
