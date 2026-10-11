using System;
using InControl;
using WandWars;
using UnityEngine;

namespace WandWarsMP.Net
{
    public struct ScriptedInput
    {
        public int Buttons;
        public Vector2 Move;
    }

    /// <summary>
    /// Samples the local player's controller into an InputState once per frame. The local device is the
    /// most recently used one (pad or the game's KeyboardDevice). A scripted source can replace it for
    /// automated tests.
    /// </summary>
    public sealed class InputCapture
    {
        private static readonly int[] ButtonBits = { InputState.A, InputState.B, InputState.X, InputState.Y };
        private byte pressCounts;

        /// <summary>Test hook: when set, supplies (buttons, move) instead of real input.</summary>
        public Func<float, ScriptedInput> Script;

        private int lastScriptButtons;

        public InputState Sample()
        {
            int buttons;
            Vector2 move;
            if (Script != null)
            {
                var s = Script(Time.realtimeSinceStartup);
                buttons = s.Buttons;
                move = s.Move;
                for (int i = 0; i < 4; i++)
                    if ((buttons & ButtonBits[i]) != 0 && (lastScriptButtons & ButtonBits[i]) == 0) Bump(i);
                lastScriptButtons = buttons;
            }
            else
            {
                var device = InputManager.ActiveDevice;
                var actions = PlayerInput.GetActionsForDevice(device);
                PlayerAction[] acts = { actions.A, actions.B, actions.X, actions.Y };
                buttons = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (acts[i].IsPressed) buttons |= ButtonBits[i];
                    if (acts[i].WasPressed) Bump(i);
                }
                if (device.MenuIsPressed) buttons |= InputState.Menu;
                // Escape is the keyboard "back" in menus; mirrored menus live on the host, so forward it as B.
                if (Sync.MenuSync.ClientMirroring && Input.GetKey(KeyCode.Escape)) buttons |= InputState.B;
                move = actions.Move.Vector;
            }

            move = Vector2.ClampMagnitude(move, 1f);
            return new InputState
            {
                Buttons = (byte)buttons,
                MoveX = (sbyte)Mathf.RoundToInt(move.x * 127f),
                MoveY = (sbyte)Mathf.RoundToInt(move.y * 127f),
                PressCounts = pressCounts,
            };
        }

        private void Bump(int i)
        {
            int c = ((pressCounts >> (i * 2)) + 1) & 3;
            pressCounts = (byte)((pressCounts & ~(3 << (i * 2))) | (c << (i * 2)));
        }
    }
}
