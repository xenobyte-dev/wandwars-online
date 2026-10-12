using System.Collections;
using HarmonyLib;
using InControl;
using UnityEngine;
using WandWars;
using WandWarsMP.Net;

namespace WandWarsMP.Sync
{
    /// <summary>
    /// Online replacement for the in-match pause menu. Uses the game's own pause dialog but never pauses
    /// the (shared) simulation. Host: RESUME / END MATCH (everyone back to the menu). Client: RESUME /
    /// LEAVE (disconnect, back to the menu). While it is open, the local player's wizard ignores input.
    /// </summary>
    [HarmonyPatch(typeof(MatchBase), "InGameMenu")]
    internal static class OnlineMatchMenu
    {
        /// <summary>Local pause dialog open (client sends neutral input; host's local players are frozen).</summary>
        public static bool Open { get; private set; }

        private static bool Prefix(MatchBase __instance, ref IEnumerator __result)
        {
            if (!MatchSync.Online) return true;
            __result = Run(__instance);
            return false;
        }

        private static IEnumerator Run(MatchBase match)
        {
            var dialog = match.PauseDialog;
            Open = false;
            dialog.CleanUp();
            dialog.AddOption("RESUME", () => SetOpen(match, false));
            if (MatchSync.IsHost)
                dialog.AddOption("END MATCH", () =>
                {
                    SetOpen(match, false);
                    MatchSync.Instance.HostAbortMatch();
                    match.QuitToMainMenu();
                });
            else
                dialog.AddOption("LEAVE", () =>
                {
                    SetOpen(match, false);
                    NetSession.Instance?.Stop();
                    match.QuitToMainMenu();
                });

            while (!match.Quit)
            {
                if (LocalMenuPressed()) SetOpen(match, !Open);
                yield return null;
            }
            SetOpen(match, false);
        }

        /// <summary>Only this machine's own controllers/keyboard open the menu (never an online player's pad).</summary>
        private static bool LocalMenuPressed()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) return true;
            foreach (var d in InputManager.Devices)
                if (!(d is NetInputDevice) && !(d is DelayedInputDevice) && d.CommandWasPressed) return true;
            return false;
        }

        private static void SetOpen(MatchBase match, bool open)
        {
            if (Open == open) return;
            Open = open;
            match.PauseDialog.Toggle(open);
            if (MatchSync.IsHost) FreezeLocalPlayers(match, open);
        }

        private static void FreezeLocalPlayers(MatchBase match, bool frozen)
        {
            foreach (var p in match.Players)
            {
                if (p.IsBot || p.InputDevice is NetInputDevice) continue;
                foreach (var e in p.Entities)
                {
                    if (e == null) continue;
                    var input = e.GetComponent<PlayerInput>();
                    if (input != null) input.enabled = !frozen;
                }
            }
        }
    }
}
