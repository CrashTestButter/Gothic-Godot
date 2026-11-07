using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Spike options from the command line (after "--", e.g. "-- --data=/path --slice=128") with Android defaults.
    /// </summary>
    public class SpikeArgs
    {
        private readonly Dictionary<string, string> _values = new();

        // Godot drops launch arguments for exported Android activities, so the Quest reads them from a file.
        private const string _androidArgsFile = "/storage/emulated/0/Android/data/com.gothic.godot/files/spike-args.txt";

        public SpikeArgs()
        {
            var args = new List<string>(OS.GetCmdlineUserArgs());
            if (OS.GetName() == "Android" && System.IO.File.Exists(_androidArgsFile))
                args.AddRange(System.IO.File.ReadAllText(_androidArgsFile)
                    .Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries));

            foreach (var arg in args)
            {
                var trimmed = arg.TrimStart('-');
                var split = trimmed.IndexOf('=');
                if (split < 0)
                    _values[trimmed] = "true";
                else
                    _values[trimmed[..split]] = trimmed[(split + 1)..];
            }
        }

        public string DataPath => Get("data", OS.GetName() == "Android"
            ? "/storage/emulated/0/Android/data/com.gothic.godot/files/Gothic1"
            : System.Environment.GetEnvironmentVariable("HOME") + "/.cache/gothic-g1-vanilla");

        public string World => Get("world", "WORLD.ZEN");
        public int SliceSize => GetInt("slice", 256);
        public float CellSize => GetFloat("cell", 64f);
        public float QuitAfterSeconds => GetFloat("quit-after", 0f);
        public string Screenshot => Get("screenshot", "");
        public bool IsVobsEnabled => Get("vobs", "true") == "true";

        public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;

        public int GetInt(string key, int fallback) =>
            _values.TryGetValue(key, out var value) && int.TryParse(value, out var result) ? result : fallback;

        public float GetFloat(string key, float fallback) =>
            _values.TryGetValue(key, out var value) &&
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
    }
}
