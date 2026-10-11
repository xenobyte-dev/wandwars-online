using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace WandWarsMP.Net
{
    /// <summary>
    /// Direct-IP transport over a single non-blocking UDP socket, with a minimal reliable-ordered channel
    /// (per-message seq, per-message ack, timed resend). Used for LAN/port-forwarded play and for
    /// local two-instance testing. Payloads must fit in one datagram (MaxPayload).
    /// </summary>
    public sealed class UdpTransport : ITransport
    {
        public const int MaxPayload = 1200;

        private const byte PktConnect = 1, PktAccept = 2, PktUnreliable = 3, PktReliable = 4, PktAck = 5,
            PktDisconnect = 6, PktKeepAlive = 7;

        private const uint Magic = 0x57574D50; // "WWMP"
        private const float TimeoutSec = 10f, KeepAliveSec = 0.5f, ConnectRetrySec = 0.5f;

        private sealed class Pending
        {
            public byte[] Packet;
            public float LastSent;
            public float FirstSent;
        }

        private sealed class Peer
        {
            public int Id;
            public IPEndPoint EndPoint;
            public bool Connected;
            public float LastHeard, LastSent;
            public ushort NextSendSeq;
            public ushort NextRecvSeq;
            public readonly Dictionary<ushort, Pending> Unacked = new Dictionary<ushort, Pending>();
            public readonly Dictionary<ushort, byte[]> OutOfOrder = new Dictionary<ushort, byte[]>();
            public float Rtt = -1;
        }

        private readonly Socket socket;
        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private readonly Dictionary<string, Peer> peersByAddr = new Dictionary<string, Peer>();
        private readonly byte[] recvBuf = new byte[2048];
        private readonly byte[] sendBuf = new byte[MaxPayload + 16];
        private readonly Func<float> clock;
        private int nextPeerId = 1;

        public bool IsHost { get; }

        public event Action<int> PeerConnected;
        public event Action<int, string> PeerDisconnected;
        public event Action<int, byte[], int, int> DataReceived;

        private UdpTransport(bool isHost, int bindPort, Func<float> clock)
        {
            IsHost = isHost;
            this.clock = clock;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Blocking = false;
            // Ignore ICMP port-unreachable resets on Windows (otherwise ReceiveFrom throws after a peer quits).
            try { socket.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch { }
            socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
        }

        public static UdpTransport Host(int port, Func<float> clock) => new UdpTransport(true, port, clock);

        public static UdpTransport Connect(string host, int port, Func<float> clock)
        {
            var t = new UdpTransport(false, 0, clock);
            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr))
                addr = Array.Find(Dns.GetHostEntry(host).AddressList, a => a.AddressFamily == AddressFamily.InterNetwork);
            var p = t.AddPeer(new IPEndPoint(addr, port), 0);
            t.SendConnect(p);
            return t;
        }

        private Peer AddPeer(IPEndPoint ep, int id)
        {
            var p = new Peer { Id = id, EndPoint = ep, LastHeard = clock() };
            peers[id] = p;
            peersByAddr[ep.ToString()] = p;
            return p;
        }

        private void RemovePeer(Peer p, string reason)
        {
            peers.Remove(p.Id);
            peersByAddr.Remove(p.EndPoint.ToString());
            if (p.Connected) PeerDisconnected?.Invoke(p.Id, reason);
        }

        public float GetRtt(int peer) => peers.TryGetValue(peer, out var p) ? p.Rtt : -1;

        // ---- sending ----

        /// <summary>Dev-only network conditions applied to outgoing packets (one-way).</summary>
        public static float SimLatencyMs, SimJitterMs, SimLossPercent;
        private static readonly Random simRng = new Random();

        private struct Delayed
        {
            public float Due;
            public byte[] Data;
            public EndPoint To;
        }

        private readonly List<Delayed> delayed = new List<Delayed>();

        private void Raw(Peer p, byte[] buf, int len)
        {
            p.LastSent = clock();
            if (SimLatencyMs > 0 || SimLossPercent > 0)
            {
                if (simRng.NextDouble() * 100 < SimLossPercent) return;
                var copy = new byte[len];
                Buffer.BlockCopy(buf, 0, copy, 0, len);
                float delay = (SimLatencyMs + (float)(simRng.NextDouble() * 2 - 1) * SimJitterMs) / 1000f;
                delayed.Add(new Delayed { Due = clock() + Math.Max(0, delay), Data = copy, To = p.EndPoint });
                return;
            }
            SendNow(buf, len, p.EndPoint);
        }

        private void SendNow(byte[] buf, int len, EndPoint to)
        {
            try { socket.SendTo(buf, 0, len, SocketFlags.None, to); }
            catch (SocketException) { /* transient (buffer full etc.); reliability layer resends */ }
        }

        private void FlushDelayed()
        {
            if (delayed.Count == 0) return;
            float now = clock();
            for (int i = 0; i < delayed.Count; i++)
            {
                if (delayed[i].Due > now) continue;
                SendNow(delayed[i].Data, delayed[i].Data.Length, delayed[i].To);
                delayed.RemoveAt(i--);
            }
        }

        private void SendConnect(Peer p)
        {
            sendBuf[0] = PktConnect;
            WriteU32(sendBuf, 1, Magic);
            Raw(p, sendBuf, 5);
        }

        private void SendControl(Peer p, byte type)
        {
            sendBuf[0] = type;
            Raw(p, sendBuf, 1);
        }

        public void Send(int peer, byte[] data, int offset, int count, bool reliable)
        {
            if (count > MaxPayload) throw new ArgumentException($"UDP payload {count} > {MaxPayload}");
            if (!peers.TryGetValue(peer, out var p) || !p.Connected) return;
            if (!reliable)
            {
                sendBuf[0] = PktUnreliable;
                Buffer.BlockCopy(data, offset, sendBuf, 1, count);
                Raw(p, sendBuf, count + 1);
                return;
            }
            ushort seq = p.NextSendSeq++;
            var pkt = new byte[count + 3];
            pkt[0] = PktReliable;
            pkt[1] = (byte)seq; pkt[2] = (byte)(seq >> 8);
            Buffer.BlockCopy(data, offset, pkt, 3, count);
            float now = clock();
            p.Unacked[seq] = new Pending { Packet = pkt, LastSent = now, FirstSent = now };
            Raw(p, pkt, pkt.Length);
        }

        public void Disconnect(int peer)
        {
            if (!peers.TryGetValue(peer, out var p)) return;
            SendControl(p, PktDisconnect);
            RemovePeer(p, "local disconnect");
        }

        public void Shutdown()
        {
            foreach (var p in new List<Peer>(peers.Values)) Disconnect(p.Id);
            socket.Close();
        }

        // ---- receiving ----

        public void Poll()
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (socket.Available > 0)
            {
                int n;
                try { n = socket.ReceiveFrom(recvBuf, ref from); }
                catch (SocketException) { break; }
                if (n > 0) Handle((IPEndPoint)from, n);
            }
            Tick();
            FlushDelayed();
        }

        private void Handle(IPEndPoint from, int n)
        {
            peersByAddr.TryGetValue(from.ToString(), out var p);
            byte type = recvBuf[0];

            if (type == PktConnect)
            {
                if (!IsHost || n < 5 || ReadU32(recvBuf, 1) != Magic) return;
                if (p == null) p = AddPeer(new IPEndPoint(from.Address, from.Port), nextPeerId++);
                p.LastHeard = clock();
                SendControl(p, PktAccept); // resent for every duplicate connect, in case accept was lost
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
                case PktUnreliable:
                    if (p.Connected) DataReceived?.Invoke(p.Id, recvBuf, 1, n - 1);
                    break;
                case PktReliable:
                    if (p.Connected && n >= 3) HandleReliable(p, n);
                    break;
                case PktAck:
                    if (n >= 3)
                    {
                        ushort seq = (ushort)(recvBuf[1] | (recvBuf[2] << 8));
                        if (p.Unacked.TryGetValue(seq, out var pend))
                        {
                            float sample = (clock() - pend.LastSent) * 1000f;
                            if (pend.LastSent == pend.FirstSent) // Karn: only un-resent samples
                                p.Rtt = p.Rtt < 0 ? sample : p.Rtt * 0.875f + sample * 0.125f;
                            p.Unacked.Remove(seq);
                        }
                    }
                    break;
                case PktDisconnect:
                    RemovePeer(p, "remote disconnect");
                    break;
            }
        }

        private void HandleReliable(Peer p, int n)
        {
            ushort seq = (ushort)(recvBuf[1] | (recvBuf[2] << 8));
            sendBuf[0] = PktAck; sendBuf[1] = recvBuf[1]; sendBuf[2] = recvBuf[2];
            Raw(p, sendBuf, 3);

            short diff = (short)(seq - p.NextRecvSeq);
            if (diff < 0) return; // duplicate
            if (diff > 0)
            {
                if (!p.OutOfOrder.ContainsKey(seq))
                {
                    var copy = new byte[n - 3];
                    Buffer.BlockCopy(recvBuf, 3, copy, 0, n - 3);
                    p.OutOfOrder[seq] = copy;
                }
                return;
            }
            p.NextRecvSeq++;
            DataReceived?.Invoke(p.Id, recvBuf, 3, n - 3);
            while (peers.ContainsKey(p.Id) && p.OutOfOrder.TryGetValue(p.NextRecvSeq, out var next))
            {
                p.OutOfOrder.Remove(p.NextRecvSeq);
                p.NextRecvSeq++;
                DataReceived?.Invoke(p.Id, next, 0, next.Length);
            }
        }

        private void Tick()
        {
            float now = clock();
            foreach (var p in new List<Peer>(peers.Values))
            {
                if (now - p.LastHeard > TimeoutSec) { RemovePeer(p, "timeout"); continue; }
                if (!p.Connected)
                {
                    if (!IsHost && now - p.LastSent > ConnectRetrySec) SendConnect(p);
                    continue;
                }
                float rto = p.Rtt < 0 ? 0.2f : Math.Max(0.05f, p.Rtt * 0.0015f + 0.02f);
                foreach (var pend in p.Unacked.Values)
                {
                    if (now - pend.LastSent < rto) continue;
                    Raw(p, pend.Packet, pend.Packet.Length);
                    pend.LastSent = now;
                }
                if (now - p.LastSent > KeepAliveSec) SendControl(p, PktKeepAlive);
            }
        }

        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static uint ReadU32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
