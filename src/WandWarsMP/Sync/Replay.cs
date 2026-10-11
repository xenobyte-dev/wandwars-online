using System;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Marks code running on a client because the host told it to. Client-side blocking patches let
    /// calls through only while replaying, so local simulation can't invent outcomes.
    /// </summary>
    public static class Replay
    {
        private static int depth;

        public static bool Active => depth > 0;

        public static Scope Begin() { depth++; return new Scope(); }

        public struct Scope : IDisposable
        {
            public void Dispose() => depth--;
        }
    }
}
