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
        BallSpawn = 25,     // u16 id, vec2 pos, vec2 vel, u8 owner, u8 normSpeed, f32 size
        BallDespawn = 26,   // u16 id
        Damage = 27,        // u8 target, u8 amount, Source, u8 preHealth, bool died, i16 postHealth
        Death = 28,         // u8 target, Source (death not caused by TakeDamage)
        Catch = 29,         // u8 player, u16 ball, vec2 ballOffset, vec2 rbCatchPos, vec2 preVel
        Launch = 30,        // u8 player, u16 ball, vec2 dir, u8 normSpeed
        Release = 31,       // u8 player, vec2 dir, u8 n, n x (u16 ball, u8 normSpeed)
        Hex = 32,           // u8 target, f32 duration
        RoundWinner = 33,   // u8 round, u8 kind, u8 winner, players (u8 num, i32 score), teams (u8 idx, i32 score)
        RoundOver = 34,     // u8 round
        ItemSpawn = 37,     // u16 id, str powerupName, vec2 pos, bool inBubble
        ItemDespawn = 38,   // u16 id
        ItemPickup = 39,    // u8 player, u16 item
        Goal = 35,          // u8 targetTeam, u16 ball, vec2 pos, vec2 dir, u8 owner
        MatchAbort = 43,
        ShotDead = 44,      // u8 caster, vec2 pos: an arcane shot hit something on the host    // host ended the match early: everyone back to the menu
        PostMatch = 36,     // host left the score screen: everyone back to the lobby

        // lobby
        LobbyState = 40,    // H->C: characters, stages, settings, slots
        LobbyPick = 41,     // C->H: u8 character (255 random), bool ready
        MenuState = 42,     // H->C: panel, boxes, ready, mode, length, stage, players (native menu mirror)
    }

    public static class Protocol
    {
        /// <summary>Bump on any wire-format change. Peers with different values refuse each other.</summary>
        public const ushort Version = 8;
        public const int DefaultPort = 27420;
    }
}
