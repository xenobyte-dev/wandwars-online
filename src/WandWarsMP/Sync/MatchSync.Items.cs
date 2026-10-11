using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>Marks a powerup item that exists because the host said so; carries its net id.</summary>
    public sealed class NetItem : MonoBehaviour
    {
        public ushort Id;
    }

    /// <summary>
    /// Host-authoritative powerups: items on the floor are spawned/despawned by the host, pickups are
    /// decided by the host and replayed, free items' positions ride in snapshots.
    /// </summary>
    public sealed partial class MatchSync
    {
        private readonly Dictionary<ushort, PowerupItem> itemsById = new Dictionary<ushort, PowerupItem>();
        private readonly List<ushort> scratchIds = new List<ushort>();
        private ushort nextItemId = 1;
        private readonly NetWriter itemWriter = new NetWriter();
        internal static bool SpawningNetItem;

        private void ResetItems()
        {
            itemsById.Clear();
            nextItemId = 1;
        }

        // ---- host ----

        internal ushort HostItemId(PowerupItem item)
        {
            var mark = item.GetComponent<NetItem>();
            if (mark != null) return mark.Id;
            mark = item.gameObject.AddComponent<NetItem>();
            mark.Id = nextItemId++;
            if (nextItemId == 0) nextItemId = 1;
            itemsById[mark.Id] = item;
            var w = itemWriter;
            w.Reset();
            w.U8((byte)Msg.ItemSpawn);
            w.U16(mark.Id);
            w.Str(item.PowerupName);
            w.Vec2(item.transform.position);
            net.Broadcast(w, true);
            return mark.Id;
        }

        private void HostTrackItems()
        {
            foreach (var item in PowerupItem.Items) HostItemId(item);
            scratchIds.Clear();
            foreach (var kv in itemsById)
            {
                var it = kv.Value;
                if (it == null) scratchIds.Add(kv.Key);            // destroyed
            }
            foreach (var id in scratchIds)
            {
                itemsById.Remove(id);
                var w = net.Begin(Msg.ItemDespawn);
                w.U16(id);
                net.Broadcast(w, true);
            }
        }

        internal void HostOnPickup(EntityInventory inv, PowerupItem item)
        {
            byte num = inv.Entity != null ? PlayerNum(inv.Entity) : NoPlayer;
            if (num == NoPlayer) return;
            ushort id = HostItemId(item);
            var w = net.Begin(Msg.ItemPickup);
            w.U8(num);
            w.U16(id);
            net.Broadcast(w, true);
        }

        private void WriteItemSnapshot(NetWriter w)
        {
            int count = 0;
            foreach (var kv in itemsById)
                if (kv.Value != null && kv.Value.isActiveAndEnabled && !kv.Value.IsInInventory) count++;
            w.U8((byte)Mathf.Min(count, 255));
            int n = 0;
            foreach (var kv in itemsById)
            {
                var it = kv.Value;
                if (it == null || !it.isActiveAndEnabled || it.IsInInventory) continue;
                if (n++ >= 255) break;
                w.U16(kv.Key);
                w.Vec2(it.transform.position);
            }
        }

        // ---- client ----

        private void ReadItemSnapshot(ref NetReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                ushort id = r.U16();
                Vector2 pos = r.Vec2();
                if (!itemsById.TryGetValue(id, out var it) || it == null || it.IsInInventory) continue;
                Vector2 cur = it.transform.position;
                Vector2 err = pos - cur;
                it.transform.position = err.magnitude > SnapDistance ? pos : cur + err * PosBlend;
            }
        }

        private void OnItemSpawn(ref NetReader r)
        {
            ushort id = r.U16();
            string name = r.Str();
            Vector2 pos = r.Vec2();
            if (itemsById.ContainsKey(id)) return;
            if (!Game.GlobalResources.Items.TryGetValue(name, out var entry) || entry.Prefab == null)
            {
                Plugin.Trace("Sync[C]: unknown item " + name);
                return;
            }
            PowerupItem item;
            SpawningNetItem = true;
            try
            {
                item = Object.Instantiate(entry.Prefab, pos, Quaternion.identity) as PowerupItem;
                item.gameObject.AddComponent<NetItem>().Id = id;
            }
            finally { SpawningNetItem = false; }
            itemsById[id] = item;
        }

        private void OnItemDespawn(ref NetReader r)
        {
            ushort id = r.U16();
            if (!itemsById.TryGetValue(id, out var it)) return;
            itemsById.Remove(id);
            if (it != null && !it.IsInInventory) Object.Destroy(it.gameObject);
        }

        private void OnItemPickup(ref NetReader r)
        {
            var pc = EntityOf(r.U8());
            ushort id = r.U16();
            if (pc == null || pc.Inventory == null || !itemsById.TryGetValue(id, out var it) || it == null) return;
            if (!it.gameObject.activeSelf) it.gameObject.SetActive(true);
            using (Replay.Begin()) pc.Inventory.AddPowerupToInventory(it, false);
            if (!it.IsInInventory) Plugin.Trace($"Sync[C]: pickup of {it.PowerupName} rejected locally");
        }
    }

    // ================= patches =================

    /// <summary>Client: items exist only when the host spawns them (drops, spawners, dispensers…).</summary>
    [HarmonyPatch(typeof(PowerupItem), "OnEnable")]
    internal static class ItemOnEnablePatch
    {
        private static bool Prefix(PowerupItem __instance)
        {
            if (!MatchSync.IsClient || Game.Match == null || MatchSync.SpawningNetItem) return true;
            if (__instance.GetComponent<NetItem>() != null) return true;
            __instance.gameObject.SetActive(false);
            Object.Destroy(__instance.gameObject);
            return false;
        }
    }

    [HarmonyPatch(typeof(EntityInventory), nameof(EntityInventory.AddPowerupToInventory))]
    internal static class PickupPatch
    {
        private static bool Prefix() => !Authority.BlockLocal;

        private static void Postfix(EntityInventory __instance, PowerupItem powerupItem)
        {
            if (Authority.HostSending && powerupItem != null && powerupItem.IsInInventory)
                MatchSync.Instance.HostOnPickup(__instance, powerupItem);
        }
    }

    [HarmonyPatch(typeof(PowerupSpawner), nameof(PowerupSpawner.SpawnRandom))]
    internal static class PowerupSpawnerPatch
    {
        private static bool Prefix() => !Authority.BlockLocal;
    }
}
