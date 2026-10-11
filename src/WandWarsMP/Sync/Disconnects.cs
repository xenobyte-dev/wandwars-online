using HarmonyLib;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Host: a client that drops mid-match is taken over by a bot (their wizard keeps playing so the
    /// match stays fair for everyone else). Client: losing the host aborts the match back to the menu.
    /// </summary>
    public sealed class Disconnects : MonoBehaviour
    {
        private NetSession net;
        private bool wasConnectedClient;

        public static void Ensure()
        {
            if (Plugin.Root.GetComponent<Disconnects>() == null) Plugin.Root.AddComponent<Disconnects>();
        }

        private void Start()
        {
            net = NetSession.Ensure();
            net.RemoteLeft += OnRemoteLeft;
        }

        private void OnRemoteLeft(RemotePlayer rp)
        {
            var match = Game.Match;
            if (match == null) return;
            foreach (var p in match.Players)
            {
                if (p.InputDevice != rp.Device) continue;
                Plugin.Trace($"Disconnects: P{(int)p.PlayerNumber + 1} ({rp.Name}) left, bot takes over");
                Traverse.Create(p).Property("IsBot").SetValue(true);
                p.BotDifficulty = 100;
                foreach (var e in p.Entities)
                {
                    if (e == null) continue;
                    var input = e.GetComponent<PlayerInput>();
                    if (input != null) Destroy(input);
                    if (e.GetComponent<EntityAI>() == null) e.gameObject.AddComponent<EntityAI>().SetDifficulty(1f);
                }
            }
        }

        private void Update()
        {
            if (net == null) return;
            bool connectedClient = net.Role == NetRole.Client && net.IsConnected;
            if (wasConnectedClient && !connectedClient && Game.Match != null)
            {
                Plugin.Trace("Disconnects: lost the host, returning to menu");
                ReturnToMenu();
            }
            wasConnectedClient = connectedClient;
        }

        public static void ReturnToMenu()
        {
            Time.timeScale = 1f;
            var game = SingletonBehaviour<Game>.Instance;
            Game.GameStateMachine.Start(new GameLobby(game.GameLobbySceneName));
        }
    }
}
