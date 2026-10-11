using System;
using System.Collections.Generic;
using InControl;
using UnityEngine;

namespace WandWarsMP.Net
{
    public enum NetRole { None, Host, Client }

    /// <summary>A remote player as seen by the host.</summary>
    public sealed class RemotePlayer
    {
        public int Peer;
        public byte Slot;
        public string Name;
        public NetInputDevice Device;
        public ushort LastInputSeq;
        public bool HasInput;
    }

    /// <summary>
    /// Owns the transport and the connection lifecycle. Host: accepts clients and exposes each one's
    /// controller as a NetInputDevice. Client: streams local input to the host every frame.
    /// Higher layers (match sync) subscribe to Message for everything not handled here.
    /// </summary>
    public sealed class NetSession : MonoBehaviour
    {
        public static NetSession Instance { get; private set; }

        public NetRole Role { get; private set; }
        public ITransport Transport { get; private set; }
        public string Status { get; private set; } = "offline";
        public string LocalName = Environment.UserName;

        /// <summary>Client only: slot assigned by host, -1 until welcomed.</summary>
        public int LocalSlot { get; private set; } = -1;
        public bool IsConnected => Role == NetRole.Host || LocalSlot >= 0;

        public readonly Dictionary<int, RemotePlayer> Remotes = new Dictionary<int, RemotePlayer>();
        public readonly InputCapture Capture = new InputCapture();

        /// <summary>Fired for message types this class doesn't consume: (peer, msg, reader positioned after id).</summary>
        public event MessageHandler Message;
        public delegate void MessageHandler(int peer, Msg msg, ref NetReader r);

        public event Action<RemotePlayer> RemoteJoined;
        public event Action<RemotePlayer> RemoteLeft;

        private readonly NetWriter w = new NetWriter();
        private ushort inputSeq;
        private float nextPing;
        private readonly Dictionary<int, float> rttMs = new Dictionary<int, float>();

        /// <summary>Smoothed ping/pong round trip in ms (includes frame-loop delay on both ends), -1 if unknown.</summary>
        public float GetRtt(int peer) => rttMs.TryGetValue(peer, out var v) ? v : -1;
        public float HostRttMs => GetRtt(0);

        public static NetSession Ensure()
        {
            if (Instance == null) Instance = Plugin.Root.AddComponent<NetSession>();
            return Instance;
        }

        public static float Clock() => Time.realtimeSinceStartup;

        public void SetStatus(string s)
        {
            Status = s;
            Plugin.Trace("Net: " + s);
        }

        public void StartHost(int port) => StartHost(UdpTransport.Host(port, Clock), $"hosting on UDP {port}");

        public void StartClient(string host, int port) =>
            StartClient(UdpTransport.Connect(host, port, Clock), $"connecting to {host}:{port}");

        public void StartHost(ITransport transport, string status)
        {
            CloseTransport();
            Transport = transport;
            Hook();
            Role = NetRole.Host;
            SetStatus(status);
        }

        public void StartClient(ITransport transport, string status)
        {
            CloseTransport();
            Transport = transport;
            Hook();
            Role = NetRole.Client;
            SetStatus(status);
        }

        /// <summary>User-initiated: close the connection and leave any Steam lobby.</summary>
        public void Stop()
        {
            CloseTransport();
            SteamLobby.Instance?.Leave();
        }

        private void CloseTransport()
        {
            if (Transport == null) return;
            foreach (var rp in Remotes.Values) DetachRemote(rp);
            Remotes.Clear();
            rttMs.Clear();
            Transport.Shutdown();
            Transport = null;
            Role = NetRole.None;
            LocalSlot = -1;
            Status = "offline";
        }

        private void Hook()
        {
            Transport.PeerConnected += OnPeerConnected;
            Transport.PeerDisconnected += OnPeerDisconnected;
            Transport.DataReceived += OnData;
        }

        private void OnDestroy() => Stop();

        // ---- sending helpers ----

        public NetWriter Begin(Msg msg)
        {
            w.Reset();
            w.U8((byte)msg);
            return w;
        }

        public void SendTo(int peer, NetWriter msg, bool reliable) =>
            Transport?.Send(peer, msg.Data, 0, msg.Length, reliable);

        /// <summary>Host: send to every welcomed client.</summary>
        public void Broadcast(NetWriter msg, bool reliable)
        {
            foreach (var peer in Remotes.Keys) SendTo(peer, msg, reliable);
        }

        // ---- lifecycle ----

        private void Update()
        {
            if (Transport == null) return;
            Transport.Poll();
            if (Transport == null) return;

            float now = Clock();
            if (now >= nextPing)
            {
                nextPing = now + 0.5f;
                var m = Begin(Msg.Ping);
                m.F32(now);
                if (Role == NetRole.Host) Broadcast(m, false); else SendTo(0, m, false);
            }

            if (Role == NetRole.Client && LocalSlot >= 0)
            {
                var m = Begin(Msg.Input);
                m.U16(inputSeq++);
                Capture.Sample().Write(m);
                SendTo(0, m, false);
            }
        }

        private void OnPeerConnected(int peer)
        {
            if (Role == NetRole.Client)
            {
                Status = "connected, handshaking";
                var m = Begin(Msg.Hello);
                m.U16(Protocol.Version);
                m.Str(Plugin.Version);
                m.Str(LocalName);
                SendTo(0, m, true);
            }
        }

        private void OnPeerDisconnected(int peer, string reason)
        {
            Plugin.Trace($"Net: peer {peer} disconnected ({reason})");
            if (Role == NetRole.Client)
            {
                Status = "disconnected: " + reason;
                LocalSlot = -1;
                return;
            }
            if (Remotes.TryGetValue(peer, out var rp))
            {
                Remotes.Remove(peer);
                DetachRemote(rp);
                RemoteLeft?.Invoke(rp);
            }
        }

        private void DetachRemote(RemotePlayer rp)
        {
            if (rp.Device != null) InputManager.DetachDevice(rp.Device);
        }

        private void OnData(int peer, byte[] data, int offset, int count)
        {
            var r = new NetReader(data, offset, count);
            Msg msg;
            try
            {
                msg = (Msg)r.U8();
                switch (msg)
                {
                    case Msg.Hello when Role == NetRole.Host: OnHello(peer, ref r); return;
                    case Msg.Welcome when Role == NetRole.Client: OnWelcome(ref r); return;
                    case Msg.Reject when Role == NetRole.Client:
                        Status = "rejected: " + r.Str();
                        Plugin.Trace("Net: " + Status);
                        return;
                    case Msg.Input when Role == NetRole.Host: OnInput(peer, ref r); return;
                    case Msg.Ping:
                    {
                        float t = r.F32();
                        var m = Begin(Msg.Pong);
                        m.F32(t);
                        SendTo(peer, m, false);
                        return;
                    }
                    case Msg.Pong:
                    {
                        float sample = (Clock() - r.F32()) * 1000f;
                        rttMs[peer] = rttMs.TryGetValue(peer, out var old) ? old * 0.8f + sample * 0.2f : sample;
                        return;
                    }
                }
                Message?.Invoke(peer, msg, ref r);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Net: bad message from {peer}: {e.Message}");
            }
        }

        private void OnHello(int peer, ref NetReader r)
        {
            ushort proto = r.U16();
            string modVersion = r.Str();
            string name = r.Str();
            if (proto != Protocol.Version)
            {
                var m = Begin(Msg.Reject);
                m.Str($"version mismatch: host {Plugin.Version} (protocol {Protocol.Version}), you {modVersion} (protocol {proto})");
                SendTo(peer, m, true);
                return;
            }
            if (Remotes.ContainsKey(peer)) return;

            byte slot = 1;
            while (SlotTaken(slot)) slot++;
            var rp = new RemotePlayer { Peer = peer, Slot = slot, Name = name, Device = new NetInputDevice("Online " + name) };
            Remotes[peer] = rp;
            InputManager.AttachDevice(rp.Device);

            var w2 = Begin(Msg.Welcome);
            w2.U8(slot);
            w2.Str(LocalName);
            SendTo(peer, w2, true);
            Plugin.Trace($"Net: {name} joined as slot {slot} (peer {peer})");
            RemoteJoined?.Invoke(rp);
        }

        private bool SlotTaken(byte slot)
        {
            foreach (var rp in Remotes.Values) if (rp.Slot == slot) return true;
            return false;
        }

        private void OnWelcome(ref NetReader r)
        {
            LocalSlot = r.U8();
            string hostName = r.Str();
            Status = $"connected to {hostName} as slot {LocalSlot}";
            Plugin.Trace("Net: " + Status);
        }

        private void OnInput(int peer, ref NetReader r)
        {
            if (!Remotes.TryGetValue(peer, out var rp)) return;
            ushort seq = r.U16();
            var s = InputState.Read(ref r);
            if (rp.HasInput && (short)(seq - rp.LastInputSeq) <= 0) return; // stale/reordered
            rp.LastInputSeq = seq;
            rp.HasInput = true;
            rp.Device.SetState(s);
        }
    }
}
