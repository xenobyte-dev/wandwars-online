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
        Scores = 46,        // absolute player (score, chickens, lives) + team scores, on change
        Respawn = 47,       // u8 player, u8 spawnPoint, bool ball, bool immunity
        EntitySpawn = 48,   // u16 id, str prefab, vec2 pos, u16 masterId (non-player entity)
        EntityDespawn = 49, // u16 id
        EntityDeath = 50,   // u16 id, Source
        Beast = 51,         // u8 player (Last Hen Standing beast pick)
        RefereeLine = 52,   // str: referee's pre-round line (keeps spawn timing identical)
        FastForward = 53,   // u8 round: host skipped the rest of the round (all humans dead)
        SubMatch = 54,      // Medley/Eggnarok: the host's next sub-match (type, level, config, teams, scores)
        TauntDone = 55,     // u8 round: host finished its post-round winner taunt
        TauntPlay = 56,     // u8 player: host's player taunts (time-freezing cinematic)
        BossBind = 57,      // u16 id, u8 bossIndex: bind both sides' identically generated boss
        EntityAbility = 58, // u16 id, u8 ability, i8 lx, i8 ly
        EntityAction = 59,  // u16 id, u8 action, vec2 arg (knight special/shield/teleport)
        Barrier = 60,       // i32 code: host finished a host-paced sequence (boss intro, victory)
        PortalEnter = 61,   // u8 player
        SpellstormStage = 62, // str: next Spellstorm stage
        CatchClaim = 64,    // client->host u16 ball, vec2 offset, vec2 preVel, vec2 catcherPos
        CatchReject = 65,   // host->client u16 ball
        WorldSnapshot = 63, // (unreliable) u32 tick, items, entities, eggs
        MatchStats = 45,    // end-of-match per-player stats (score screen)
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
        public const ushort Version = 19;
        public const int DefaultPort = 27420;
    }
}
