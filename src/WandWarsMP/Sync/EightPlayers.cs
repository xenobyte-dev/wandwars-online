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
        private static void Postfix(GameUISettings __instance) => Ensure(__instance);

        /// <summary>Saved settings from disk can still be 4 long; call before anything indexes them.</summary>
        internal static void Ensure(GameUISettings __instance)
        {
            if (__instance == null) return;
            var configs = __instance.CharacterSelectionConfig;
            if (configs != null && configs.Length < EightPlayers.MaxPlayers)
            {
                int old = configs.Length;
                System.Array.Resize(ref configs, EightPlayers.MaxPlayers);
                for (int i = old; i < configs.Length; i++)
                    configs[i] = new CharacterSelectionConfig { CharacterIndex = i, Difficulty = -1 };
                __instance.CharacterSelectionConfig = configs;
                Traverse.Create(__instance).Method("UpdateSkinSelectionArray", new[] { typeof(CharacterSelectionConfig[]) }).GetValue(new object[] { configs });
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

    /// <summary>
    /// Versus character select: 4 extra boxes cloned at load. They are shown (one row of 8, scaled) only
    /// while an online session is active; offline keeps the game's 4-box screen untouched.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSelection), "Awake")]
    internal static class CharacterSelectAwakePatch
    {
        internal static CharacterSelectionBox[] All;
        internal static CharacterSelection Panel;

        private static void Prefix(CharacterSelection __instance)
        {
            if (__instance.GetType() != typeof(CharacterSelection)) return;
            var existing = __instance.GetComponentsInChildren<CharacterSelectionBox>(true);
            if (existing.Length != 4) return;
            var template = existing[3];
            int sibling = template.transform.GetSiblingIndex();
            for (int i = 4; i < EightPlayers.MaxPlayers; i++)
            {
                var go = EightPlayers.CloneUnder(template.gameObject, template.transform.parent);
                go.name = "character_box_wwmp" + i;
                go.transform.SetSiblingIndex(++sibling);
            }
        }

        private static void Postfix(CharacterSelection __instance)
        {
            if (__instance.GetType() != typeof(CharacterSelection)) return;
            Panel = __instance;
            All = Traverse.Create(__instance).Field("boxes").GetValue<CharacterSelectionBox[]>();
            // First Enter activates the panel (and runs Awake) after Enter's prefix: configure here too.
            CharacterSelectEnterPatch.Configure(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterSelection), nameof(CharacterSelection.Enter))]
    internal static class CharacterSelectEnterPatch
    {
        private static float? baseSpacing;

        internal static bool EightActive => Net.NetSession.Instance != null && Net.NetSession.Instance.IsConnected;

        private static void Prefix(CharacterSelection __instance)
        {
            UISettingsResizePatch.Ensure(Game.UISettings);
            Configure(__instance);
        }

        internal static void Configure(CharacterSelection __instance)
        {
            if (__instance != CharacterSelectAwakePatch.Panel) return;
            var all = CharacterSelectAwakePatch.All;
            if (all == null || all.Length < EightPlayers.MaxPlayers) return;
            int n = EightActive ? EightPlayers.MaxPlayers : 4;
            var boxes = all.Take(n).ToArray();
            var t = Traverse.Create(__instance);
            t.Field("boxes").SetValue(boxes);
            t.Field("modules").SetValue(boxes.Select(b => b.BotSelectionModule).ToArray());
            for (int i = 0; i < all.Length; i++)
            {
                all[i].gameObject.SetActive(i < n);
                all[i].transform.localScale = n > 4 ? new Vector3(0.56f, 0.56f, 1f) : Vector3.one;
            }
            var hlg = __instance.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                if (baseSpacing == null) baseSpacing = hlg.spacing;
                hlg.spacing = n > 4 ? baseSpacing.Value * 0.5f : baseSpacing.Value;
            }
        }
    }

    /// <summary>Box i becomes player number i; boxes 4..7 must be players 5..8 (4 is "None").</summary>
    [HarmonyPatch(typeof(CharacterSelection), "ProcessCharacterSelectionData")]
    internal static class RenumberPlayersPatch
    {
        private static void Postfix(List<Player> __result)
        {
            foreach (var p in __result)
                if ((int)p.PlayerNumber >= (int)PlayerNumber.None) p.PlayerNumber = (PlayerNumber)((int)p.PlayerNumber + 1);
        }
    }

    /// <summary>Team select: CPU portraits exist for 4 slots; wrap them for 8.</summary>
    [HarmonyPatch(typeof(TeamPanel), nameof(TeamPanel.Enter))]
    internal static class TeamPanelPortraitsPatch
    {
        private static void Prefix(TeamPanel __instance)
        {
            UISettingsResizePatch.Ensure(Game.UISettings);
            var src = __instance.CPUPortraits;
            int n = GameLobby.Players?.Count ?? 0;
            if (src == null || src.Length == 0 || src.Length >= n) return;
            var arr = new Sprite[n];
            for (int i = 0; i < n; i++) arr[i] = src[i % src.Length];
            __instance.CPUPortraits = arr;
        }
    }

    /// <summary>
    /// Bot setup in character/team select starts its "first free slot" search at the hard-coded 3, which
    /// would pick a human slot when the bots are in slots 5-8. Start at the last slot instead.
    /// </summary>
    [HarmonyPatch]
    internal static class FirstSelectedIndexPatch
    {
                private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(CharacterSelection), typeof(TeamPanel) })
            {
                var iter = type.GetNestedTypes(AccessTools.all).FirstOrDefault(t => t.Name.Contains("Run") && typeof(System.Collections.IEnumerator).IsAssignableFrom(t));
                if (iter != null) yield return AccessTools.Method(iter, "MoveNext");
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].opcode != System.Reflection.Emit.OpCodes.Ldc_I4_3) continue;
                var next = list[i + 1];
                if (next.opcode == System.Reflection.Emit.OpCodes.Stfld && next.operand is System.Reflection.FieldInfo f && f.Name.Contains("firstSelectedIndex"))
                {
                    list[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(FirstSelectedIndexPatch), nameof(Start))).MoveLabelsFrom(list[i]);
                    Plugin.Trace("EightPlayers: firstSelectedIndex patched in " + f.DeclaringType.Name);
                }
            }
            return list;
        }

        private static int Start()
        {
            var cs = GameLobby.CurrentPanel;
            if (cs is CharacterSelection)
                return Mathf.Max(0, (Traverse.Create(cs).Field("boxes").GetValue<CharacterSelectionBox[]>()?.Length ?? 4) - 1);
            if (cs is TeamPanel)
                return Mathf.Max(0, (Traverse.Create(cs).Field("boxes").GetValue<List<TeamSelectionBox>>()?.Count ?? 4) - 1);
            return 3;
        }
    }

    /// <summary>Eggnarok above 4 players: King of the Coop (one zone per player) is left out.</summary>
    [HarmonyPatch(typeof(Eggnarok), MethodType.Constructor, new[] { typeof(string), typeof(List<Player>), typeof(MatchConfig) })]
    internal static class EggnarokRosterPatch
    {
        private static object fullList;

        private static void Prefix(List<Player> playerList)
        {
            var field = Traverse.Create(typeof(Eggnarok)).Field("possibleMatches");
            var list = field.GetValue() as System.Collections.IList;
            if (list == null) return;
            if (fullList == null) fullList = list;
            var full = (System.Collections.IList)fullList;
            if (playerList.Count <= 4) { field.SetValue(full); return; }
            var filtered = (System.Collections.IList)System.Activator.CreateInstance(full.GetType());
            foreach (var m in full)
                if ((MatchType)Traverse.Create(m).Field("MatchTypeEnum").GetValue() != MatchType.KingOfTheCoop) filtered.Add(m);
            field.SetValue(filtered);
        }
    }

    /// <summary>Medley above 4 players: skip King of the Coop inside its Eggnarok slot (advance past it).</summary>
    [HarmonyPatch(typeof(Medley), "AllowMatchGivenPlayers")]
    internal static class MedleyRosterPatch
    {
        private static void Postfix(List<Player> players, object matchInfo, ref bool __result)
        {
            if (!__result || players.Count <= 4) return;
            var t = Traverse.Create(matchInfo);
            if ((MatchType)t.Property("MatchTypeEnum").GetValue() != MatchType.KingOfTheCoop) return;
            t.Method("OnMatchUsed").GetValue();
            __result = false;
        }
    }

    /// <summary>
    /// Hexout / Hen Hunt HUD prefabs hold 4 modules indexed by player number. Above 4 players the match
    /// instantiates an expanded runtime copy instead (kept under an inactive holder so it never shows).
    /// </summary>
    [HarmonyPatch]
    internal static class ModeHudPrefabPatch
    {
        private static readonly Dictionary<string, HexoutHud> originals = new Dictionary<string, HexoutHud>();
        private static readonly Dictionary<string, HexoutHud> expanded = new Dictionary<string, HexoutHud>();
        private static GameObject holder;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(HexOut), nameof(HexOut.Start));
            yield return AccessTools.Method(typeof(HenHunt), nameof(HenHunt.Start));
        }

        private static void Prefix(MatchBase __instance)
        {
            bool big = EightPlayers.MaxNumber(__instance.Players) >= 4;
            Swap("HexoutHudPrefab", big);
            Swap("HenHuntHudPrefab", big);
        }

        private static void Swap(string field, bool big)
        {
            var gr = Game.GlobalResources;
            var f = Traverse.Create(gr).Field(field);
            var current = f.GetValue<HexoutHud>();
            if (current == null) return;
            if (!originals.ContainsKey(field)) originals[field] = current;
            var original = originals[field];
            if (!big) { f.SetValue(original); return; }
            if (!expanded.TryGetValue(field, out var big9) || big9 == null)
            {
                if (holder == null)
                {
                    holder = new GameObject("wwmp_hud_templates");
                    holder.SetActive(false);
                    Object.DontDestroyOnLoad(holder);
                }
                big9 = Object.Instantiate(original);
                big9.transform.SetParent(holder.transform, false);
                var modules = big9.Modules.ToList();
                int baseCount = modules.Count;
                while (modules.Count < EightPlayers.MaxPlayers + 1)
                {
                    // Index 4 is the unused dummy; players 5-8 sit beside the corners of players 1-4.
                    int idx = modules.Count;
                    var template = modules[idx == 4 ? baseCount - 1 : (idx - 5) % baseCount];
                    var go = EightPlayers.CloneUnder(template.gameObject, template.transform.parent);
                    go.name = template.name + "_wwmp" + idx;
                    var rt = go.transform as RectTransform;
                    var parentRt = template.transform.parent as RectTransform;
                    if (idx > 4 && rt != null)
                    {
                        float width = parentRt != null && parentRt.rect.width > 0 ? parentRt.rect.width : 480f;
                        float dir = rt.anchorMin.x < 0.5f ? 1f : -1f;
                        rt.anchoredPosition += new Vector2(dir * width * 0.2f, 0f);
                    }
                    go.SetActive(false); // SetScoreProvider activates the ones in use
                    modules.Add(go.GetComponent<HUDModule>());
                }
                big9.Modules = modules.ToArray();
                expanded[field] = big9;
                Plugin.Trace($"EightPlayers: {field} expanded to {modules.Count} modules");
            }
            f.SetValue(big9);
        }
    }

    /// <summary>Versus intro: the portrait strip fits ~4 players; shrink it for more.</summary>
    [HarmonyPatch(typeof(VersusScreen), nameof(VersusScreen.Prepare))]
    internal static class VersusScreenScalePatch
    {
        private static void Postfix(VersusScreen __instance, MatchBase match)
        {
            if (__instance.Container == null) return;
            int n = match.Players.Count;
            float s = n > 4 ? Mathf.Min(1f, 8.5f / (2 * n - 1)) : 1f;
            __instance.Container.localScale = new Vector3(s, s, 1f);
        }
    }
}
