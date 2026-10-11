using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;
using WandWars;

namespace WandWarsMP.Dev
{
    /// <summary>
    /// Headless-ish test driver. Activated by a file "wwmp_autotest.txt" in the game root.
    /// File lines: key=value (mode=census|..., seconds=30, quit=true).
    /// Boots straight into a bot-only match, samples the scene, writes BepInEx/wwmp_census.txt, quits.
    /// </summary>
    public class DevHarness : MonoBehaviour
    {
        private Dictionary<string, string> cfg;

        public static void TryStart(GameObject host)
        {
            string path = Path.Combine(Paths.GameRootPath, "wwmp_autotest.txt");
            if (!File.Exists(path)) { Plugin.Trace("DevHarness: no " + path); return; }
            var h = host.AddComponent<DevHarness>();
            h.cfg = File.ReadAllLines(path)
                .Select(l => l.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
            Plugin.Trace("DevHarness active: " + string.Join(", ", h.cfg.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
        }

        private string Get(string key, string def) => cfg.TryGetValue(key, out var v) ? v : def;

        private IEnumerator Start()
        {
            // Wait for the game to reach the main menu.
            float t0 = Time.realtimeSinceStartup;
            string last = null;
            while (Game.GameStateMachine == null || !(Game.State is GameLobby))
            {
                string cur = Game.GameStateMachine == null ? "no-fsm" : (Game.State == null ? "null" : Game.State.GetType().Name);
                if (cur != last) { Plugin.Trace("DevHarness: waiting, state=" + cur); last = cur; }
                yield return null;
            }
            Plugin.Trace($"DevHarness: lobby reached after {Time.realtimeSinceStartup - t0:F1}s");
            yield return new WaitForSeconds(2f);

            string stage = Get("stage", "");
            if (stage == "") stage = Stages.GetRandomStage();
            int bots = int.Parse(Get("bots", "4"));
            var players = new List<Player>();
            var chars = Game.GlobalResources.GetCharacters().ToList();
            for (int i = 0; i < bots; i++)
                players.Add(new Player(chars[i % chars.Count], (PlayerNumber)i, null, null, isBot: true));

            Plugin.Trace($"DevHarness: starting Deathmatch on {stage} with {bots} bots");
            Game.GameStateMachine.Start(new Deathmatch(stage, players, new MatchConfig { MatchType = MatchType.Deathmatch }));

            while (!(Game.State is MatchBase)) yield return null;
            yield return new WaitForSeconds(5f);

            if (Get("mode", "census") == "census")
                yield return StartCoroutine(Census(float.Parse(Get("seconds", "20"))));

            if (Get("quit", "true") == "true") Application.Quit();
        }

        private IEnumerator Census(float seconds)
        {
            var sb = new StringBuilder();
            var known = new HashSet<int>();
            var spawnedNames = new Dictionary<string, int>();
            int samples = 0, created = 0, destroyed = 0, maxLive = 0;
            float end = Time.realtimeSinceStartup + seconds;

            foreach (var go in FindObjectsOfType<GameObject>()) known.Add(go.GetInstanceID());

            while (Time.realtimeSinceStartup < end)
            {
                var now = FindObjectsOfType<GameObject>();
                var ids = new HashSet<int>();
                foreach (var go in now)
                {
                    int id = go.GetInstanceID();
                    ids.Add(id);
                    if (!known.Contains(id))
                    {
                        created++;
                        string n = go.transform.parent == null ? go.name : go.transform.root.name + "/" + go.name;
                        spawnedNames[n] = spawnedNames.TryGetValue(n, out var c) ? c + 1 : 1;
                    }
                }
                destroyed += known.Count(k => !ids.Contains(k));
                known = ids;
                maxLive = Math.Max(maxLive, now.Length);
                samples++;
                yield return null;
            }

            sb.AppendLine($"Scene: {Application.loadedLevelName}  samples={samples} over {seconds}s");
            sb.AppendLine($"Active GameObjects now={known.Count} max={maxLive} created={created} destroyed={destroyed}");
            sb.AppendLine();

            sb.AppendLine("== Component counts (active) ==");
            foreach (var g in FindObjectsOfType<Component>().GroupBy(c => c.GetType().FullName).OrderByDescending(g => g.Count()))
                sb.AppendLine($"{g.Count(),5}  {g.Key}");
            sb.AppendLine();

            sb.AppendLine("== Renderers (enabled) by type / shader ==");
            foreach (var g in FindObjectsOfType<Renderer>().Where(r => r.enabled)
                         .GroupBy(r => r.GetType().Name + " | " + (r.sharedMaterial ? r.sharedMaterial.shader.name : "null"))
                         .OrderByDescending(g => g.Count()))
                sb.AppendLine($"{g.Count(),5}  {g.Key}");
            sb.AppendLine();

            sb.AppendLine("== Cameras ==");
            foreach (var cam in FindObjectsOfType<Camera>())
                sb.AppendLine($"{cam.name} depth={cam.depth} ortho={cam.orthographic} size={cam.orthographicSize} mask={cam.cullingMask} rt={(cam.targetTexture ? cam.targetTexture.name : "-")}");
            sb.AppendLine();

            sb.AppendLine("== Spawned during sample (by root/name) ==");
            foreach (var kv in spawnedNames.OrderByDescending(k => k.Value).Take(80))
                sb.AppendLine($"{kv.Value,5}  {kv.Key}");
            sb.AppendLine();

            sb.AppendLine("== Hierarchy roots ==");
            foreach (var go in FindObjectsOfType<GameObject>().Where(g => g.transform.parent == null).OrderBy(g => g.name))
                sb.AppendLine($"{go.name}  (children={go.GetComponentsInChildren<Transform>().Length - 1})");

            string outPath = Path.Combine(Paths.BepInExRootPath, "wwmp_census.txt");
            File.WriteAllText(outPath, sb.ToString());
            Plugin.Trace("DevHarness: census written to " + outPath);
        }
    }
}
