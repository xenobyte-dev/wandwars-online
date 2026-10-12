using System.Collections.Generic;
using InControl;
using UnityEngine;

namespace WandWarsMP.Net
{
    /// <summary>
    /// Host fairness: replays a local controller's state after a delay, so the host's inputs reach the
    /// authoritative simulation about as late as online players' inputs do (half their round trip).
    /// </summary>
    public sealed class DelayedInputDevice : InputDevice
    {
        private static readonly InputControlType[] Controls =
        {
            InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp,
            InputControlType.LeftStickDown, InputControlType.Action1, InputControlType.Action2,
            InputControlType.Action3, InputControlType.Action4,
        };

        private struct Sample
        {
            public float Time;
            public float[] Values;
        }

        public readonly InputDevice Source;
        /// <summary>Current delay in seconds (updated live from measured pings).</summary>
        public float Delay;

        private readonly Queue<Sample> queue = new Queue<Sample>();
        private float[] current = new float[Controls.Length];

        public DelayedInputDevice(InputDevice source) : base(source.Name)
        {
            Source = source;
            foreach (var c in Controls) AddControl(c, c.ToString());
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            var values = new float[Controls.Length];
            for (int i = 0; i < Controls.Length; i++)
            {
                var src = Source.GetControl(Controls[i]);
                values[i] = src != null ? src.Value : 0f;
            }
            queue.Enqueue(new Sample { Time = now, Values = values });
            while (queue.Count > 0 && now - queue.Peek().Time >= Delay) current = queue.Dequeue().Values;
            for (int i = 0; i < Controls.Length; i++)
                UpdateWithValue(Controls[i], current[i], updateTick, deltaTime);
            Commit(updateTick, deltaTime);
        }
    }
}
