namespace WandWarsMP.Net
{
    /// <summary>Message ids (first byte of every transport payload).</summary>
    public enum Msg : byte
    {
        // handshake
        Hello = 1,          // C->H: u16 protocol, str modVersion, str name
        Welcome = 2,        // H->C: u8 slot, str hostName
        Reject = 3,         // H->C: str reason

        // realtime
        Input = 10,         // C->H (unreliable): u16 seq, InputState
        Ping = 11,          // either way (unreliable): f32 senderTime
        Pong = 12,          // echo: f32 senderTime

        // match sync (host -> clients)
        MatchStart = 20,    // MatchDesc
        RoundSpawn = 21,    // u8 round, u8 n, n x u8 spawnPointIndex, u8 ballPlayer(255 none)
        Frame = 22,         // (unreliable) u32 tick, u8 n, n x (u8 player, i8 mx, i8 my)
        Ability = 23,       // u8 player, u8 ability, i8 lx, i8 ly (lastMoveDirection)
        Snapshot = 24,      // (unreliable) u32 tick, players, balls
    }

    public static class Protocol
    {
        /// <summary>Bump on any wire-format change. Peers with different values refuse each other.</summary>
        public const ushort Version = 1;
        public const int DefaultPort = 27420;
    }
}
