using System;

namespace UnityEngine
{
    /// <summary>Frame time for the shared core; the Godot side sets it every frame (GothicCoreHost).</summary>
    public static class Time
    {
        public static float time { get; set; }
        public static float deltaTime { get; set; }
        public static float unscaledTime { get; set; }
        public static float unscaledDeltaTime { get; set; }
        public static float realtimeSinceStartup => (float)(DateTime.UtcNow - Start).TotalSeconds;
        public static int frameCount { get; set; }
        public static float timeScale { get; set; } = 1f;
        public static float fixedDeltaTime { get; set; } = 0.02f;

        private static readonly DateTime Start = DateTime.UtcNow;

        public static void Advance(float delta)
        {
            deltaTime = delta * timeScale;
            unscaledDeltaTime = delta;
            time += deltaTime;
            unscaledTime += delta;
            frameCount++;
        }
    }

    public static class Random
    {
        private static System.Random _random = new();

        public static void InitState(int seed) => _random = new System.Random(seed);
        public static float value => (float)_random.NextDouble();
        public static float Range(float min, float max) => min + (float)_random.NextDouble() * (max - min);
        public static int Range(int min, int max) => max > min ? _random.Next(min, max) : min;
        public static Vector3 insideUnitSphere
        {
            get
            {
                Vector3 v;
                do
                    v = new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
                while (v.sqrMagnitude > 1f);
                return v;
            }
        }
    }

    /// <summary>Unity's Debug routed to a sink the Godot side sets (GD.Print / PushWarning / PushError).</summary>
    public static class Debug
    {
        public static Action<string> LogSink = Console.WriteLine;
        public static Action<string> WarningSink = Console.WriteLine;
        public static Action<string> ErrorSink = Console.Error.WriteLine;

        public static void Log(object message) => LogSink(message?.ToString());
        public static void Log(object message, Object context) => LogSink(message?.ToString());
        public static void LogWarning(object message) => WarningSink(message?.ToString());
        public static void LogWarning(object message, Object context) => WarningSink(message?.ToString());
        public static void LogError(object message) => ErrorSink(message?.ToString());
        public static void LogError(object message, Object context) => ErrorSink(message?.ToString());
        public static void LogException(Exception exception) => ErrorSink(exception.ToString());
        public static void Assert(bool condition, string message = null)
        {
            if (!condition)
                ErrorSink("Assertion failed: " + message);
        }
    }

    [Serializable]
    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a = 1f)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static Color white => new(1f, 1f, 1f);
        public static Color black => new(0f, 0f, 0f);
        public static Color clear => new(0f, 0f, 0f, 0f);
        public static Color red => new(1f, 0f, 0f);
        public static Color green => new(0f, 1f, 0f);
        public static Color blue => new(0f, 0f, 1f);
        public static Color yellow => new(1f, 0.92f, 0.016f);
        public static Color gray => new(0.5f, 0.5f, 0.5f);
        public static Color Lerp(Color a, Color b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
        }
        public static Color operator *(Color c, float d) => new(c.r * d, c.g * d, c.b * d, c.a * d);
        public static Color operator *(Color a, Color b) => new(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
        public static Color operator +(Color a, Color b) => new(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a);
        public static implicit operator Color(Color32 c) => new(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
    }

    [Serializable]
    public struct Color32
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;

        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static implicit operator Color32(Color c) => new((byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255));
    }

    [Serializable]
    public struct Bounds
    {
        public Vector3 center;
        public Vector3 extents;

        public Bounds(Vector3 center, Vector3 size)
        {
            this.center = center;
            extents = size * 0.5f;
        }

        public Vector3 size { get => extents * 2f; set => extents = value * 0.5f; }
        public Vector3 min { get => center - extents; set => SetMinMax(value, max); }
        public Vector3 max { get => center + extents; set => SetMinMax(min, value); }

        public void SetMinMax(Vector3 newMin, Vector3 newMax)
        {
            extents = (newMax - newMin) * 0.5f;
            center = newMin + extents;
        }

        public void Encapsulate(Vector3 point) => SetMinMax(Vector3.Min(min, point), Vector3.Max(max, point));
        public void Encapsulate(Bounds bounds)
        {
            Encapsulate(bounds.min);
            Encapsulate(bounds.max);
        }

        public bool Contains(Vector3 point) =>
            point.x >= min.x && point.x <= max.x && point.y >= min.y && point.y <= max.y && point.z >= min.z && point.z <= max.z;

        public bool Intersects(Bounds other) =>
            min.x <= other.max.x && max.x >= other.min.x && min.y <= other.max.y && max.y >= other.min.y &&
            min.z <= other.max.z && max.z >= other.min.z;

        public Vector3 ClosestPoint(Vector3 point) => Vector3.Max(min, Vector3.Min(max, point));
        public float SqrDistance(Vector3 point) => (ClosestPoint(point) - point).sqrMagnitude;
    }

    public enum TextureFormat
    {
        Alpha8 = 1,
        RGB24 = 3,
        RGBA32 = 4,
        ARGB32 = 5,
        RGB565 = 7,
        DXT1 = 10,
        DXT5 = 12,
        BGRA32 = 14
    }
}
