using InControl;

namespace WandWarsMP.Net
{
    /// <summary>Controller state as sent over the wire: 4 face buttons + a quantized move stick.</summary>
    public struct InputState
    {
        public const int A = 1, B = 2, X = 4, Y = 8, Menu = 16;

        public byte Buttons;
        public sbyte MoveX, MoveY;      // -127..127
        /// <summary>Per-button press counters (2 bits each, A/B/X/Y). Lets the receiver recover taps
        /// that began and ended between two received packets.</summary>
        public byte PressCounts;

        public bool Has(int b) => (Buttons & b) != 0;
        public int PressCount(int buttonIndex) => (PressCounts >> (buttonIndex * 2)) & 3;

        public void Write(NetWriter w) { w.U8(Buttons); w.I8(MoveX); w.I8(MoveY); w.U8(PressCounts); }

        public static InputState Read(ref NetReader r) =>
            new InputState { Buttons = r.U8(), MoveX = r.I8(), MoveY = r.I8(), PressCounts = r.U8() };
    }

    /// <summary>
    /// Virtual InControl controller fed from the network. Attached on the host for each remote player,
    /// so the game treats them exactly like a local pad (joining character select, PlayerInput, menus).
    /// </summary>
    public sealed class NetInputDevice : InputDevice
    {
        private static readonly InputControlType[] Buttons =
            { InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4 };

        private InputState current;
        private byte lastCounts;
        // Taps detected via counters are held for one InControl update so WasPressed/WasReleased fire.
        private int pendingTaps;

        public NetInputDevice(string name) : base(name)
        {
            AddControl(InputControlType.LeftStickLeft, "Left Stick Left");
            AddControl(InputControlType.LeftStickRight, "Left Stick Right");
            AddControl(InputControlType.LeftStickUp, "Left Stick Up");
            AddControl(InputControlType.LeftStickDown, "Left Stick Down");
            AddControl(InputControlType.Action1, "A");
            AddControl(InputControlType.Action2, "B");
            AddControl(InputControlType.Action3, "X");
            AddControl(InputControlType.Action4, "Y");
            AddControl(InputControlType.Menu, "Menu");
        }

        public void SetState(InputState s)
        {
            for (int i = 0; i < 4; i++)
            {
                int newPresses = (s.PressCount(i) - ((lastCounts >> (i * 2)) & 3)) & 3;
                if (newPresses > 0 && !s.Has(1 << i)) pendingTaps |= 1 << i;
            }
            lastCounts = s.PressCounts;
            current = s;
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            float x = current.MoveX / 127f, y = current.MoveY / 127f;
            UpdateWithValue(InputControlType.LeftStickLeft, x < 0 ? -x : 0, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftStickRight, x > 0 ? x : 0, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftStickDown, y < 0 ? -y : 0, updateTick, deltaTime);
            UpdateWithValue(InputControlType.LeftStickUp, y > 0 ? y : 0, updateTick, deltaTime);
            for (int i = 0; i < 4; i++)
                UpdateWithState(Buttons[i], current.Has(1 << i) || (pendingTaps & (1 << i)) != 0, updateTick, deltaTime);
            UpdateWithState(InputControlType.Menu, current.Has(InputState.Menu), updateTick, deltaTime);
            pendingTaps = 0;
            Commit(updateTick, deltaTime);
        }
    }
}
