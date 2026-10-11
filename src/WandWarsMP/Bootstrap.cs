using System;
using HarmonyLib;
using UnityEngine;

namespace WandWarsMP
{
    /// <summary>Everything that touches game types starts here, after Unity has loaded Assembly-CSharp.</summary>
    internal static class Bootstrap
    {
        internal static void Init()
        {
            try
            {
                new Harmony(Plugin.Guid).PatchAll(typeof(Bootstrap).Assembly);
                Plugin.Trace("Harmony patches applied.");
            }
            catch (Exception e)
            {
                Plugin.Trace("Patching failed: " + e);
            }
            if (UnityEngine.Object.FindObjectOfType<WandWars.Game>() != null) OnGameBoot();
        }

        internal static void OnGameBoot()
        {
            if (Plugin.Root != null) return;
            Plugin.Root = new GameObject("_WandWarsMP");
            UnityEngine.Object.DontDestroyOnLoad(Plugin.Root);
            Plugin.Trace($"Game booted. Dev mode: {Dev.DevMode.Enabled}");
            Net.NetSession.Ensure();
            Sync.MatchSync.Ensure();
            Sync.OnlineLobby.Ensure();
            Sync.Disconnects.Ensure();
            UI.NativeOnlineMenu.Ensure();
            Sync.MenuSync.Ensure();
            Plugin.Root.AddComponent<UI.OnlinePanel>();
            try { Dev.DevHarness.TryStart(Plugin.Root); }
            catch (Exception e) { Plugin.Trace("DevHarness failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(WandWars.Game), "Awake")]
    internal static class GameAwakePatch
    {
        private static void Postfix() => Bootstrap.OnGameBoot();
    }
}
