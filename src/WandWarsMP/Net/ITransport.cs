using System;

namespace WandWarsMP.Net
{
    /// <summary>
    /// Connection-oriented message transport. Peer ids are transport-local ints: on a client the host is
    /// always peer 0; on the host, clients are 1..N. All events fire from Poll() on the main thread.
    /// Reliable messages are delivered in order; unreliable ones may drop or reorder.
    /// </summary>
    public interface ITransport
    {
        bool IsHost { get; }

        event Action<int> PeerConnected;
        event Action<int, string> PeerDisconnected;
        event Action<int, byte[], int, int> DataReceived;

        void Poll();
        void Send(int peer, byte[] data, int offset, int count, bool reliable);
        void Disconnect(int peer);
        void Shutdown();

        /// <summary>Smoothed round-trip time in ms, or -1 if unknown.</summary>
        float GetRtt(int peer);
    }
}
