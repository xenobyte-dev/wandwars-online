using System.IO;
using BepInEx;

namespace WandWarsMP.Dev
{
    /// <summary>Dev mode is enabled by a "wwmp_dev.txt" marker in the game root.</summary>
    internal static class DevMode
    {
        public static readonly bool Enabled = File.Exists(Path.Combine(Paths.GameRootPath, "wwmp_dev.txt"));
    }
}
