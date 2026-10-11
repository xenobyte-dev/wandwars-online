using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace WandWarsMP
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "wandwarsmp";
        public const string Name = "Wand Wars Online";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;

        /// <summary>Persistent object holding all mod MonoBehaviours. Created once the game boots.</summary>
        internal static GameObject Root;

        private static readonly string TracePath = Path.Combine(Paths.BepInExRootPath, "wwmp_trace.log");

        /// <summary>Log + append to an unbuffered trace file (BepInEx's disk log is lost if the process is killed).</summary>
        internal static void Trace(string msg)
        {
            Log.LogInfo(msg);
            try { File.AppendAllText(TracePath, "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine); }
            catch { }
        }

        // BepInEx.cfg entrypoint must be Camera..cctor. The default (Application..cctor) runs inside Unity's
        // script-assembly reload; touching game types or patching there breaks script registration
        // ("referenced script is missing"). Camera..cctor fires after the first scene's Awake, so
        // Game.Awake has already run by the time we patch.
        private void Awake()
        {
            Log = Logger;
            try { File.Delete(TracePath); } catch { }
            Log.LogInfo($"{Name} {Version} loaded.");
            Bootstrap.Init();
        }
    }
}
