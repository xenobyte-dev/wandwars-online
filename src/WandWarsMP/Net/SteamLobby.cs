using System;
using Steamworks;

namespace WandWarsMP.Net
{
    /// <summary>
    /// Steam lobby flow: host creates a friends-only lobby and opens the invite overlay; friends join via
    /// invite / "Join Game" in the friends list (GameLobbyJoinRequested_t) or a +connect_lobby launch arg.
    /// Lobby membership gates who may open a P2P session with the host.
    /// </summary>
    public sealed class SteamLobby
    {
        public const string KeyMod = "wwmp";
        public const int MaxMembers = Sync.EightPlayers.MaxPlayers;

        public static SteamLobby Instance { get; private set; }

        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        public bool InLobby => Lobby != CSteamID.Nil;

        private readonly CallResult<LobbyCreated_t> created;
        private readonly CallResult<LobbyEnter_t> entered;
        private readonly Steamworks.Callback<GameLobbyJoinRequested_t> joinRequested;
        private readonly Steamworks.Callback<LobbyChatUpdate_t> memberChanged;

        private SteamLobby()
        {
            created = CallResult<LobbyCreated_t>.Create(OnCreated);
            entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            joinRequested = Steamworks.Callback<GameLobbyJoinRequested_t>.Create(r => Join(r.m_steamIDLobby));
            memberChanged = Steamworks.Callback<LobbyChatUpdate_t>.Create(OnMemberChanged);
        }

        /// <summary>Call once Steam is initialized. Also handles being launched from an invite.</summary>
        public static void Init()
        {
            if (Instance != null) return;
            bool steamUp;
            try { steamUp = SteamManager.Initialized; } catch { steamUp = false; }
            if (!steamUp) { Plugin.Trace("Steam: not initialized, lobbies unavailable"); return; }
            Instance = new SteamLobby();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                    Instance.Join(new CSteamID(id));
        }

        public void Host()
        {
            Leave();
            created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxMembers));
            NetSession.Ensure().SetStatus("creating Steam lobby…");
        }

        public void Join(CSteamID lobby)
        {
            Leave();
            Plugin.Trace("Steam: joining lobby " + lobby);
            entered.Set(SteamMatchmaking.JoinLobby(lobby));
            NetSession.Ensure().SetStatus("joining Steam lobby…");
        }

        public bool IsMember(CSteamID user)
        {
            if (!InLobby) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == user) return true;
            return false;
        }

        public void Invite()
        {
            if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        public void Leave()
        {
            if (!InLobby) return;
            SteamMatchmaking.LeaveLobby(Lobby);
            Lobby = CSteamID.Nil;
        }

        private void OnCreated(LobbyCreated_t r, bool ioFailure)
        {
            if (ioFailure || r.m_eResult != EResult.k_EResultOK)
            {
                NetSession.Ensure().SetStatus("lobby creation failed: " + r.m_eResult);
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, KeyMod, Protocol.Version.ToString());
            var transport = SteamTransport.Host(NetSession.Clock);
            NetSession.Ensure().StartHost(transport, "hosting Steam lobby");
            Plugin.Trace("Steam: lobby created " + Lobby);
            Invite();
        }

        private void OnEntered(LobbyEnter_t r, bool ioFailure)
        {
            if (ioFailure || r.m_EChatRoomEnterResponse != 1 /* k_EChatRoomEnterResponseSuccess */)
            {
                NetSession.Ensure().SetStatus("could not join lobby");
                return;
            }
            Lobby = new CSteamID(r.m_ulSteamIDLobby);
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(Lobby);
            if (owner == SteamUser.GetSteamID()) return; // our own lobby (host path handled in OnCreated)

            string ver = SteamMatchmaking.GetLobbyData(Lobby, KeyMod);
            if (ver != Protocol.Version.ToString())
            {
                NetSession.Ensure().SetStatus(string.IsNullOrEmpty(ver)
                    ? "that lobby isn't running Wand Wars Online"
                    : $"version mismatch (lobby protocol {ver}, yours {Protocol.Version}) — update the mod");
                Leave();
                return;
            }
            NetSession.Ensure().StartClient(SteamTransport.Connect(owner, NetSession.Clock),
                "connecting to " + SteamFriends.GetFriendPersonaName(owner));
        }

        private void OnMemberChanged(LobbyChatUpdate_t u)
        {
            if (!InLobby || u.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            var transport = NetSession.Instance?.Transport as SteamTransport;
            if (transport != null && transport.IsHost) transport.AllowPeer(new CSteamID(u.m_ulSteamIDUserChanged));
        }
    }
}
