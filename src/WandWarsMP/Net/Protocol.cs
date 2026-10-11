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
    }

    public static class Protocol
    {
        /// <summary>Bump on any wire-format change. Peers with different values refuse each other.</summary>
        public const ushort Version = 1;
        public const int DefaultPort = 27420;
    }
}
