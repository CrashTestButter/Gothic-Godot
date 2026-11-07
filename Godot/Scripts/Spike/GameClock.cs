using System;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Gothic's time of day: one day lasts 6000 real seconds (Gothic-Unity's GameTimeService: 14.4 game seconds per
    /// real second), times --time-speed. Raises MinuteChanged once per game minute; Daylight is 0 at night and 1 by
    /// day with dawn 5-7 and dusk 19-21.
    /// </summary>
    public partial class GameClock : Node
    {
        public const double GameSecondsPerSecond = 86400.0 / 6000.0;

        private double _minutes;
        private int _lastMinute = -1;

        public float Speed { get; set; } = 1f;
        public int MinuteOfDay => (int)_minutes % 1440;
        public int Hour => MinuteOfDay / 60;
        public float HourOfDay => (float)(_minutes % 1440 / 60.0);

        public float Daylight
        {
            get
            {
                var hour = HourOfDay;
                return Smooth(5f, 7f, hour) * (1f - Smooth(19f, 21f, hour));
            }
        }

        public event Action<int> MinuteChanged;

        public void SetTime(int hour, int minute = 0)
        {
            _minutes = hour * 60 + minute;
            _lastMinute = MinuteOfDay;
        }

        public override void _Process(double delta)
        {
            _minutes = (_minutes + delta * GameSecondsPerSecond * Speed / 60.0) % 1440.0;
            var minute = MinuteOfDay;
            if (minute == _lastMinute)
                return;
            _lastMinute = minute;
            MinuteChanged?.Invoke(minute);
        }

        private static float Smooth(float from, float to, float value)
        {
            var t = Mathf.Clamp((value - from) / (to - from), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
