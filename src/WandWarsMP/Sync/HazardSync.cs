using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Staple;
using WandWarsMP.Net;
using WandWars;
using Random = UnityEngine.Random;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Stage hazards (DJ Inferno, Beast Rock, Owl Lake, turrets...) make their choices with Unity's
    /// global random generator, so every machine rolled different numbers: the DJ flew elsewhere,
    /// fireballs and blizzards came at other times, rock/collider layouts differed.
    /// Online, each hazard method call (and each resume of a hazard coroutine) runs on its own random
    /// stream, seeded from the shared match seed, the hazard's identity in the level, the method and
    /// how many times that method has run - identical on every machine.
    /// </summary>
    internal static class HazardRng
    {
        internal static readonly Type[] HazardTypes =
        {
            typeof(DJInferno), typeof(BeastRockShuffler), typeof(OwlLakeColliderManager),
            typeof(OwlLakeBlizzard), typeof(Turret),
        };

        private sealed class Frame { public int Seed; public int Nested; }

        private static readonly Stack<Frame> stack = new Stack<Frame>();
        private static readonly Dictionary<long, int> counters = new Dictionary<long, int>();
        private static readonly Dictionary<int, int> instanceKeys = new Dictionary<int, int>();
        private static int countersSeed;
        private static int globalMix = Environment.TickCount;

        internal static bool Active => MatchSync.Online && Game.Match != null;

        /// <summary>Same on every machine: the hazard's place in the level hierarchy and start position.</summary>
        internal static int InstanceKey(MonoBehaviour mb)
        {
            int id = mb.GetInstanceID();
            if (instanceKeys.TryGetValue(id, out int key)) return key;
            var t = mb.transform;
            var path = new System.Text.StringBuilder();
            for (var x = t; x != null; x = x.parent) path.Insert(0, "/" + x.name + "#" + x.GetSiblingIndex());
            Vector3 p = t.position;
            key = Hash(Hash(path.ToString().GetHashCode() ^ mb.GetType().Name.GetHashCode(), Mathf.RoundToInt(p.x * 10)), Mathf.RoundToInt(p.y * 10));
            instanceKeys[id] = key;
            return key;
        }

        internal static void Enter(MonoBehaviour owner, string site)
        {
            if (!Active || owner == null) { stack.Push(null); return; }
            int seedNow = MatchSync.Instance != null ? MatchSync.Instance.CurrentSeed : 0;
            if (seedNow != countersSeed) { counters.Clear(); instanceKeys.Clear(); countersSeed = seedNow; }
            int inst = InstanceKey(owner);
            long ckey = ((long)inst << 32) ^ (uint)site.GetHashCode();
            counters.TryGetValue(ckey, out int n);
            counters[ckey] = n + 1;
            int seed = Hash(Hash(Hash(seedNow, inst), site.GetHashCode()), n);
            stack.Push(new Frame { Seed = seed });
            Random.seed = seed;
        }

        internal static void Exit()
        {
            if (stack.Count == 0) return;
            var f = stack.Pop();
            if (f == null) return;
            // Inside an outer hazard call: continue on a deterministic stream; else back to "random".
            Frame outer = null;
            foreach (var o in stack) { if (o != null) { outer = o; } break; }
            if (outer != null) Random.seed = Hash(outer.Seed, ++outer.Nested + 1000);
            else Random.seed = Hash(Environment.TickCount, ++globalMix);
        }

        private static int Hash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b;
                h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
                return (int)h;
            }
        }
    }

    /// <summary>Wraps hazard methods and hazard coroutine steps in their own random stream.</summary>
    [HarmonyPatch]
    internal static class HazardRngPatch
    {
        private static readonly HashSet<string> PerFrame = new HashSet<string> { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };
        private static readonly Dictionary<Type, FieldInfo> iteratorOwner = new Dictionary<Type, FieldInfo>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in HazardRng.HazardTypes)
            {
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || PerFrame.Contains(m.Name) || m.GetMethodBody() == null) continue;
                    if (m.Name.StartsWith("add_") || m.Name.StartsWith("remove_") || m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                    yield return m;
                }
                foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(nested)) continue;
                    var owner = nested.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(f => f.FieldType == type);
                    var move = AccessTools.Method(nested, "MoveNext");
                    if (owner == null || move == null) continue;
                    iteratorOwner[nested] = owner;
                    yield return move;
                }
            }
        }

        private static void Prefix(object __instance, MethodBase __originalMethod)
        {
            MonoBehaviour owner = __instance as MonoBehaviour;
            if (owner == null && __instance != null && iteratorOwner.TryGetValue(__instance.GetType(), out var f))
                owner = f.GetValue(__instance) as MonoBehaviour;
            HazardRng.Enter(owner, __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
        }

        private static Exception Finalizer(Exception __exception)
        {
            HazardRng.Exit();
            return __exception;
        }
    }

    /// <summary>
    /// DJ Inferno picks its next waypoint inside FixedUpdate (a per-frame method): do that pick on the
    /// DJ's own stream, counted per waypoint reached instead of per physics step.
    /// </summary>
    [HarmonyPatch(typeof(DJInferno), "FixedUpdate")]
    internal static class DJWaypointPatch
    {
        private static readonly AccessTools.FieldRef<DJInferno, Transform> current = AccessTools.FieldRefAccess<DJInferno, Transform>("currentWayPoint");

        private static void Prefix(DJInferno __instance)
        {
            if (!HazardRng.Active || __instance.State != DJInferno.DJState.Moving) return;
            var wp = current(__instance);
            if (wp == null || Vector2.Distance(__instance.transform.position, wp.position) >= 0.3f) return;
            HazardRng.Enter(__instance, "DJInferno.waypoint");
            try { current(__instance) = __instance.WayPoints.PickRandom(); }
            finally { HazardRng.Exit(); }
        }
    }

    /// <summary>Clients: dispenser loot comes from the host (synced items), not a local roll.</summary>
    [HarmonyPatch(typeof(PowerupDispenser), nameof(PowerupDispenser.Dispense))]
    internal static class DispenserClientPatch
    {
        private static bool Prefix(ref List<PowerupItem> __result)
        {
            if (!Authority.BlockLocal) return true;
            __result = new List<PowerupItem>();
            return false;
        }
    }
}

namespace WandWarsMP.Sync
{
    public sealed partial class MatchSync
    {
        private float hazardAccum;
        private DJInferno[] djCache;
        private float djCacheTime, nextDjTrace, djErrSum;
        private int djErrN;

        private DJInferno[] DJs()
        {
            if (djCache == null || Time.unscaledTime - djCacheTime > 2f)
            {
                djCache = UnityEngine.Object.FindObjectsOfType<DJInferno>();
                djCacheTime = Time.unscaledTime;
            }
            return djCache;
        }

        /// <summary>Host: moving hazards' positions a few times a second (safety net for drift).</summary>
        private void HostSendHazards()
        {
            hazardAccum += Time.unscaledDeltaTime;
            if (hazardAccum < 0.2f) return;
            hazardAccum = 0f;
            var djs = DJs();
            if (djs.Length == 0) return;
            var w = net.Begin(Msg.HazardState);
            w.U8((byte)djs.Length);
            foreach (var dj in djs)
            {
                w.I32(dj != null ? HazardRng.InstanceKey(dj) : 0);
                w.Vec2(dj != null ? (Vector2)dj.transform.position : Vector2.zero);
            }
            net.Broadcast(w, false);
        }

        private void OnHazardState(ref NetReader r)
        {
            int n = r.U8();
            var djs = DJs();
            for (int i = 0; i < n; i++)
            {
                int key = r.I32();
                Vector2 pos = r.Vec2();
                foreach (var dj in djs)
                {
                    if (dj == null || HazardRng.InstanceKey(dj) != key) continue;
                    var rb = dj.GetComponent<Rigidbody2D>();
                    Vector2 err = pos - (Vector2)dj.transform.position;
                    djErrSum += err.magnitude; djErrN++;
                    if (Dev.DevMode.Enabled && Time.unscaledTime > nextDjTrace)
                    {
                        nextDjTrace = Time.unscaledTime + 2f;
                        Plugin.Trace($"Sync[C]: DJ drift avg {djErrSum / djErrN:F2} now {err.magnitude:F2} state={dj.State}");
                        djErrSum = 0; djErrN = 0;
                    }
                    if (err.magnitude > 3f) { dj.transform.position = pos; if (rb != null) rb.position = pos; }
                    else if (rb != null) rb.position += err * 0.2f;
                }
            }
        }
    }
}
