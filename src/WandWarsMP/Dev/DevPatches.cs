using HarmonyLib;
using WandWars;

namespace WandWarsMP.Dev
{
    /// <summary>Automated runs: skip the first-run keyboard controls screen (it waits for a key press).</summary>
    [HarmonyPatch(typeof(GameLobby), nameof(GameLobby.Start))]
    internal static class SkipControlsScreenPatch
    {
        private static void Postfix()
        {
            if (DevHarness.Active) Traverse.Create(typeof(GameLobby)).Field("keyboardsOnly").SetValue(false);
        }
    }
}
