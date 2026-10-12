using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>Identity of an egg: index of its spawner in position order (same on every machine).</summary>
    public sealed class NetEgg : MonoBehaviour
    {
        public byte Key;
    }

    /// <summary>
    /// King of the Coop: eggs are spawned by both sides (mirrored), identified by spawner order; the host
    /// owns their physics and the hold zones' progress. Clients don't score zones locally.
    /// </summary>
    public sealed partial class MatchSync
    {
        internal int CurrentSeed;

        private static List<T> ByPosition<T>(IEnumerable<T> items) where T : Component =>
            items.Where(x => x != null)
                 .OrderBy(x => Mathf.Round(x.transform.position.x * 10f))
                 .ThenBy(x => Mathf.Round(x.transform.position.y * 10f)).ToList();

        internal static byte EggKey(EggSpawner spawner)
        {
            var list = ByPosition(Object.FindObjectsOfType<EggSpawner>());
            int i = list.IndexOf(spawner);
            return (byte)Mathf.Max(0, i);
        }

        private void WriteEggSnapshot(NetWriter w)
        {
            var eggs = Egg.All.Where(e => e != null && e.GetComponent<NetEgg>() != null).ToList();
            w.U8((byte)eggs.Count);
            foreach (var e in eggs)
            {
                var rb = e.GetComponent<Rigidbody2D>();
                w.U8(e.GetComponent<NetEgg>().Key);
                w.Vec2(rb != null ? rb.position : (Vector2)e.transform.position);
                w.Vec2(rb != null ? rb.velocity : Vector2.zero);
            }
            var zones = ByPosition(EggHoldZone.All);
            w.U8((byte)zones.Count);
            foreach (var z in zones)
            {
                w.F32(z.TimeInsideZone.Value);
                w.Bool(z.IsEggInside);
                w.Bool(z.Enabled);
                w.U8((byte)z.PlayerNumber);
            }
        }

        private void ReadEggSnapshot(ref NetReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                byte key = r.U8();
                Vector2 pos = r.Vec2(), vel = r.Vec2();
                var egg = Egg.All.FirstOrDefault(e => e != null && e.GetComponent<NetEgg>() != null && e.GetComponent<NetEgg>().Key == key);
                var rb = egg != null ? egg.GetComponent<Rigidbody2D>() : null;
                if (rb == null) { if (egg != null) egg.transform.position = pos; continue; }
                Vector2 err = pos - rb.position;
                if (err.magnitude > SnapDistance) rb.position = pos; else rb.position += err * PosBlend;
                rb.velocity = vel;
            }
            int nz = r.U8();
            var zones = ByPosition(EggHoldZone.All);
            for (int i = 0; i < nz; i++)
            {
                float t = r.F32();
                bool inside = r.Bool(), enabled = r.Bool();
                var pn = (PlayerNumber)r.U8();
                if (i >= zones.Count) continue;
                var z = zones[i];
                z.TimeInsideZone.Value = t;
                z.IsEggInside = inside;
                z.Enabled = enabled;
                if (z.PlayerNumber != pn)
                {
                    z.PlayerNumber = pn;
                    var owner = PlayerOf((byte)pn);
                    if (owner != null) z.SetColor(owner.Color);
                }
            }
        }
    }

    [HarmonyPatch(typeof(EggSpawner), nameof(EggSpawner.SpawnEgg))]
    internal static class EggSpawnPatch
    {
        private static void Postfix(EggSpawner __instance, Egg __result)
        {
            if (!MatchSync.Online || __result == null) return;
            __result.gameObject.AddComponent<NetEgg>().Key = MatchSync.EggKey(__instance);
        }
    }

    /// <summary>Client: zones are scored by the host only.</summary>
    [HarmonyPatch(typeof(Egg), "Update")]
    internal static class EggUpdatePatch
    {
        private static bool Prefix() => !(MatchSync.IsClient && Game.Match != null);
    }

    /// <summary>Both sides: same RNG state when King of the Coop picks the 2-player zone layout.</summary>
    [HarmonyPatch(typeof(KingOfTheCoop), nameof(KingOfTheCoop.Start))]
    internal static class KotcSeedPatch
    {
        private static void Prefix()
        {
            if (MatchSync.Online) Random.seed = MatchSync.Instance.CurrentSeed;
        }
    }
}
