using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using WandWars;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Lifts the game's 4-player limits to 8.
    /// Player numbers: the game's PlayerNumber.None is the value 4, so players 5-8 use numbers 5-8
    /// (4 is skipped); anything indexed by player number is sized 9 with a hidden dummy at index 4.
    /// All changes here are deterministic so host and clients build identical structures.
    /// </summary>
    public static class EightPlayers
    {
        public const int MaxPlayers = 8;

        /// <summary>Player number for the i-th player of a list (0-based): 0..3, then 5..8.</summary>
        public static PlayerNumber NumberFor(int index) => (PlayerNumber)(index < 4 ? index : index + 1);

        /// <summary>Unity 5.3 has no Instantiate(original, parent): clone and keep local layout.</summary>
        public static GameObject CloneUnder(GameObject src, Transform parent)
        {
            var go = Object.Instantiate(src);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = src.transform.localPosition;
            go.transform.localRotation = src.transform.localRotation;
            go.transform.localScale = src.transform.localScale;
            var srt = src.transform as RectTransform;
            var rt = go.transform as RectTransform;
            if (srt != null && rt != null)
            {
                rt.anchorMin = srt.anchorMin; rt.anchorMax = srt.anchorMax; rt.pivot = srt.pivot;
                rt.anchoredPosition = srt.anchoredPosition; rt.sizeDelta = srt.sizeDelta;
            }
            return go;
        }

        public static int MaxNumber(IEnumerable<Player> players) =>
            players.Select(p => (int)p.PlayerNumber).Where(n => n != (int)PlayerNumber.None).DefaultIfEmpty(0).Max();
    }

    /// <summary>Saved per-slot settings arrays (character select, teams) sized for 8.</summary>
    [HarmonyPatch(typeof(GameUISettings), nameof(GameUISettings.InitializeDefaults))]
    internal static class UISettingsResizePatch
    {
        private static void Postfix(GameUISettings __instance)
        {
            var configs = __instance.CharacterSelectionConfig;
            if (configs != null && configs.Length < EightPlayers.MaxPlayers)
            {
                int old = configs.Length;
                System.Array.Resize(ref configs, EightPlayers.MaxPlayers);
                for (int i = old; i < configs.Length; i++)
                    configs[i] = new CharacterSelectionConfig { CharacterIndex = i, Difficulty = -1 };
                __instance.CharacterSelectionConfig = configs;
                Traverse.Create(__instance).Method("UpdateSkinSelectionArray", new[] { typeof(CharacterSelectionConfig[]) }).GetValue(configs);
            }
            var teams = __instance.TeamSelectionIndeces;
            if (teams != null && teams.Length < EightPlayers.MaxPlayers)
            {
                int old = teams.Length;
                System.Array.Resize(ref teams, EightPlayers.MaxPlayers);
                for (int i = old; i < teams.Length; i++) teams[i] = i % 2;
                __instance.TeamSelectionIndeces = teams;
            }
        }
    }

    /// <summary>
    /// Levels have ~4 spawn points. Before a match with more players starts, add points next to the
    /// existing ones (fixed offset pattern, validated against the play area) — deterministic, so every
    /// machine gets the same points in the same order. New points carry home numbers 5..8.
    /// </summary>
    [HarmonyPatch]
    internal static class SpawnPointSynthesisPatch
    {
        private static readonly Vector2[] Offsets =
        {
            new Vector2(1.6f, 0f), new Vector2(-1.6f, 0f), new Vector2(0f, 1.6f), new Vector2(0f, -1.6f),
            new Vector2(1.6f, 1.6f), new Vector2(-1.6f, -1.6f), new Vector2(1.6f, -1.6f), new Vector2(-1.6f, 1.6f),
            new Vector2(3f, 0f), new Vector2(-3f, 0f),
        };

        // The level's PlayerSpawn can be re-created between rounds, so check before every spawn sequence.
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MatchBase), nameof(MatchBase.Start));
            yield return AccessTools.Method(typeof(MatchBase), "Spawn");
        }

        private static void Prefix(MatchBase __instance) => Ensure(__instance.PlayerSpawn, __instance.Players.Count);

        internal static void Ensure(PlayerSpawn spawn, int needed)
        {
            if (spawn == null || spawn.SpawnPoints == null || spawn.SpawnPoints.Length == 0) return;
            var points = spawn.SpawnPoints.ToList();
            if (points.Count >= needed) return;

            var play = SingletonBehaviour<PlayArea>.Instance;
            int originals = points.Count;
            int home = 5;
            for (int k = 0; points.Count < needed && k < originals * Offsets.Length; k++)
            {
                var src = points[k % originals];
                var off = Offsets[(k / originals) % Offsets.Length];
                Vector3 pos = src.transform.position + (Vector3)off;
                if (play != null && !play.OverlapsPlayArea(pos)) continue;
                if (points.Any(p => Vector2.Distance(p.transform.position, pos) < 1.2f)) continue;
                var cloneGo = EightPlayers.CloneUnder(src.gameObject, src.transform.parent);
                cloneGo.transform.position = pos;
                var clone = cloneGo.GetComponent<PlayerSpawnPoint>();
                clone.name = src.name + "_wwmp" + points.Count;
                clone.StartingPlayerNumber = (PlayerNumber)home++;
                points.Add(clone);
            }
            // Fallback: never leave the spawn sequence short (it indexes points by player).
            for (int k = 0; points.Count < needed; k++) points.Add(points[k % originals]);
            Traverse.Create(spawn).Property("SpawnPoints").SetValue(points.ToArray());
            Plugin.Trace($"EightPlayers: {originals} -> {points.Count} spawn points");
        }
    }

    [HarmonyPatch(typeof(PlayerSpawn), "Awake")]
    internal static class PlayerSpawnAwakePatch
    {
        private static void Postfix(PlayerSpawn __instance)
        {
            var mb = Game.Match;
            if (mb != null && mb.Players != null) SpawnPointSynthesisPatch.Ensure(__instance, mb.Players.Count);
        }
    }

    /// <summary>In-match HUD modules are indexed by player number: expand to 9 (index 4 = hidden dummy).</summary>
    [HarmonyPatch(typeof(MatchBase), "SetHelpers")]
    internal static class HudModulesPatch
    {
        private static void Postfix(MatchBase __instance)
        {
            var rsm = Traverse.Create(__instance).Field("roundScoreModule").GetValue<RoundScoreModule>();
            if (rsm == null || rsm.Modules == null) return;
            int count = __instance.Players.Count;
            float scale = count > 4 ? 4.5f / count : 1f; // the score column fits ~4 rows
            rsm.transform.localScale = new Vector3(scale, scale, 1f);
            int maxNum = EightPlayers.MaxNumber(__instance.Players);
            if (maxNum < rsm.Modules.Length) return;
            var modules = rsm.Modules.ToList();
            var template = modules[modules.Count - 1];
            while (modules.Count <= maxNum)
            {
                var go = EightPlayers.CloneUnder(template.gameObject, template.transform.parent);
                go.name = template.name + "_wwmp" + modules.Count;
                var m = go.GetComponent<HUDModule>();
                // HUDModule.Awake moved the template's score text to its parent; give the clone its own.
                if (template.ScoreText != null)
                    m.ScoreText = EightPlayers.CloneUnder(template.ScoreText.gameObject, template.ScoreText.transform.parent).GetComponent<Text>();
                go.SetActive(false);
                // Awake doesn't run on an inactive clone; set what it would have.
                var t = Traverse.Create(m);
                t.Field("rectTransform").SetValue(go.transform as RectTransform);
                t.Field("audioSource").SetValue(go.GetComponent<AudioSource>());
                if (m.Portrait != null) t.Field("portraitPos").SetValue(m.Portrait.rectTransform.anchoredPosition);
                modules.Add(m);
            }
            Traverse.Create(rsm).Property("Modules").SetValue(modules.ToArray());

            Plugin.Trace($"EightPlayers: HUD modules -> {modules.Count}");
        }
    }

    /// <summary>Score screen: 8 panels, and the 4-slot banner math neutralized and scaled for >4 players.</summary>
    [HarmonyPatch(typeof(ScoreScreen), nameof(ScoreScreen.Show))]
    internal static class ScoreScreenPanelsPatch
    {
        private static float? originalBanner;

        private static void Prefix(ScoreScreen __instance, MatchBase match)
        {
            int n = match.Players.Count;
            if (originalBanner == null) originalBanner = __instance.BannerWidth;
            if (n <= 4)
            {
                __instance.BannerWidth = originalBanner.Value;
                __instance.PanelsGroup.transform.localScale = Vector3.one;
                return;
            }
            var panels = __instance.ScorePanels.ToList();
            var template = panels[panels.Count - 1];
            while (panels.Count < n)
            {
                var clone = EightPlayers.CloneUnder(template.gameObject, template.transform.parent);
                clone.SetActive(false);
                panels.Add(clone.GetComponent<PlayerScorePanel>());
            }
            __instance.ScorePanels = panels.ToArray();
            __instance.BannerWidth = 0f; // (4 - n) * BannerWidth would grow the banner off-screen
            float s = 6f / n;
            __instance.PanelsGroup.transform.localScale = new Vector3(s, s, 1f);
        }
    }

    /// <summary>The game only knows ordinal suffixes up to 4th.</summary>
    [HarmonyPatch(typeof(PlayerScorePanel), nameof(PlayerScorePanel.Setup))]
    internal static class PlaceSuffixPatch
    {
        private static void Postfix(PlayerScorePanel __instance, int place)
        {
            if (place > 4 && __instance.Place != null && __instance.Place.text == place.ToString())
                __instance.Place.text = place + "th";
        }
    }
}
