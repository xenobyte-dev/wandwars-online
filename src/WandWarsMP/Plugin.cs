using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace WandWarsMP
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "wandwarsmp";
        public const string Name = "Wand Wars Online";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony(Guid).PatchAll();
            Log.LogInfo($"{Name} {Version} loaded. Steam initialized: {Steamworks.SteamAPI.IsSteamRunning()}");
        }
    }
}
