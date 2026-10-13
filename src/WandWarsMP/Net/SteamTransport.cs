using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace WandWarsMP.Net
{
    /// <summary>
    /// Steam P2P transport (ISteamNetworking): NAT traversal and relay fallback come from Steam.
    /// Same peer-id semantics as UdpTransport. The host only accepts sessions from members of its lobby
    /// (via AllowPeer) so random Steam users can't connect.
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        private const byte PktConnect = 1, PktAccept = 2, PktData = 3, PktDisconnect = 6;
        private const int Channel = 0;
        private const float TimeoutSec = 10f, ConnectRetrySec = 0.5f;

        private sealed class Peer
        {
            public int Id;
            public CSteamID Steam;
            public bool Connected;
            public float LastHeard, LastSent;
        }

        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private readonly Dictionary<ulong, Peer> bySteam = new Dictionary<ulong, Peer>();
        private readonly HashSet<ulong> allowed = new HashSet<ulong>();
        private readonly Func<float> clock;
        private byte[] recvBuf = new byte[4096];
        private byte[] sendBuf = new byte[4096];
        private int nextPeerId = 1;
        private Steamworks.Callback<P2PSessionRequest_t> sessionRequest;
        private Steamworks.Callback<P2PSessionConnectFail_t> connectFail;

        public bool IsHost { get; }

        public event Action<int> PeerConnected;
        public event Action<int, string> PeerDisconnected;
        public event Action<int, byte[], int, int> DataReceived;

        private SteamTransport(bool isHost, Func<float> clock)
        {
            IsHost = isHost;
            this.clock = clock;
            SteamNetworking.AllowP2PPacketRelay(true);
            sessionRequest = Steamworks.Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            connectFail = Steamworks.Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
        }

        public static SteamTransport Host(Func<float> clock) => new SteamTransport(true, clock);

        public static SteamTransport Connect(CSteamID host, Func<float> clock)
        {
            var t = new SteamTransport(false, clock);
            t.allowed.Add(host.m_SteamID);
            var p = t.AddPeer(host, 0);
            t.SendControl(p, PktConnect, true);
            return t;
        }

        /// <summary>Host: permit session requests from this user (call for each lobby member).</summary>
        public void AllowPeer(CSteamID id) => allowed.Add(id.m_SteamID);

        public CSteamID GetSteamId(int peer) => peers.TryGetValue(peer, out var p) ? p.Steam : CSteamID.Nil;

        public float GetRtt(int peer) => -1; // measured by NetSession ping/pong

        private Peer AddPeer(CSteamID id, int peerId)
        {
            var p = new Peer { Id = peerId, Steam = id, LastHeard = clock() };
            peers[peerId] = p;
            bySteam[id.m_SteamID] = p;
            return p;
        }

        private void RemovePeer(Peer p, string reason)
        {
            peers.Remove(p.Id);
            bySteam.Remove(p.Steam.m_SteamID);
            SteamNetworking.CloseP2PSessionWithUser(p.Steam);
            if (p.Connected) PeerDisconnected?.Invoke(p.Id, reason);
        }

        private bool IsAllowed(CSteamID id) =>
            allowed.Contains(id.m_SteamID) || (SteamLobby.Instance != null && SteamLobby.Instance.IsMember(id));

        private void OnSessionRequest(P2PSessionRequest_t req)
        {
            if (IsAllowed(req.m_steamIDRemote))
                SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            else
                Plugin.Log.LogWarning($"Steam: ignored P2P request from non-lobby user {req.m_steamIDRemote}");
        }

        private void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            if (bySteam.TryGetValue(fail.m_steamIDRemote.m_SteamID, out var p))
                RemovePeer(p, "steam p2p error " + fail.m_eP2PSessionError);
        }

        private void SendControl(Peer p, byte type, bool reliable)
        {
            sendBuf[0] = type;
            SteamNetworking.SendP2PPacket(p.Steam, sendBuf, 1, reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable, Channel);
            p.LastSent = clock();
        }

        public void Send(int peer, byte[] data, int offset, int count, bool reliable)
        {
            if (!peers.TryGetValue(peer, out var p) || !p.Connected) return;
            if (sendBuf.Length < count + 1) Array.Resize(ref sendBuf, count + 1);
            sendBuf[0] = PktData;
            Buffer.BlockCopy(data, offset, sendBuf, 1, count);
            SteamNetworking.SendP2PPacket(p.Steam, sendBuf, (uint)(count + 1),
                reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay, Channel);
            p.LastSent = clock();
        }

        public void Disconnect(int peer)
        {
            if (!peers.TryGetValue(peer, out var p)) return;
            SendControl(p, PktDisconnect, true);
            RemovePeer(p, "local disconnect");
        }

        public void Shutdown()
        {
            foreach (var p in new List<Peer>(peers.Values)) Disconnect(p.Id);
            sessionRequest?.Unregister();
            connectFail?.Unregister();
        }

        public void Poll()
        {
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, Channel))
            {
                if (recvBuf.Length < size) recvBuf = new byte[size];
                if (!SteamNetworking.ReadP2PPacket(recvBuf, size, out uint n, out CSteamID from, Channel)) break;
                if (n > 0) Handle(from, (int)n);
            }
            Tick();
        }

        private void Handle(CSteamID from, int n)
        {
            bySteam.TryGetValue(from.m_SteamID, out var p);
            byte type = recvBuf[0];
            if (type == PktConnect)
            {
                if (!IsHost || !IsAllowed(from)) return;
                if (p == null) p = AddPeer(from, nextPeerId++);
                p.LastHeard = clock();
                SendControl(p, PktAccept, true);
                if (!p.Connected) { p.Connected = true; PeerConnected?.Invoke(p.Id); }
                return;
            }
            if (p == null) return;
            p.LastHeard = clock();
            switch (type)
            {
                case PktAccept:
                    if (!p.Connected) { p.Connected = true; PeerConnected?.Invoke(p.Id); }
                    break;
                case PktData:
                    if (p.Connected) DataReceived?.Invoke(p.Id, recvBuf, 1, n - 1);
                    break;
                case PktDisconnect:
                    RemovePeer(p, "remote disconnect");
                    break;
            }
        }

        private void Tick()
        {
            float now = clock();
            foreach (var p in new List<Peer>(peers.Values))
            {
                if (now - p.LastHeard > TimeoutSec) { RemovePeer(p, "timeout"); continue; }
                if (!p.Connected && !IsHost && now - p.LastSent > ConnectRetrySec) SendControl(p, PktConnect, true);
            }
        }
    }
}
