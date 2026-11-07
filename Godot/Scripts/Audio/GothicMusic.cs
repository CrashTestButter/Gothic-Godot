using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DirectMusic;
using Godot;
using GothicGodot.Spike;
using ZenKit;
using ZenKit.Daedalus;
using ZenKit.Vobs;
using Performance = DirectMusic.Performance;

namespace GothicGodot.Audio
{
    /// <summary>
    /// Gothic's DirectMusic score like Gothic-Unity's MusicDomain: themes (C_MUSICTHEME in MUSIC.DAT) rendered by
    /// dmusic, picked by the music zone the listener is in (the zone playing stays while he is inside, else the last
    /// zone containing him in world order, else the world's default zone), day or night, falling back night to day.
    /// dmusic renders PCM on its own thread (like Unity's audio callback in Gothic-Unity) into an AudioStreamGenerator
    /// in small chunks, so a theme change is heard at the next measure / beat instead of after a long buffer, and the
    /// main thread doesn't pay for the synthesizer.
    /// </summary>
    public partial class GothicMusic : Node
    {
        private const int _mixRate = 44100;
        private const int _chunkFrames = 1024;
        private const float _bufferSeconds = 0.2f;
        private const double _zoneCheckSeconds = 0.5;

        private readonly Dictionary<string, MusicThemeInstance> _themes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Segment> _segments = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _brokenSegments = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(IZoneMusic Zone, Aabb Box)> _zones = new();
        private readonly float[] _pcm = new float[_chunkFrames * 2];
        private readonly Vector2[] _frames = new Vector2[_chunkFrames];
        private readonly Stopwatch _renderWatch = new();
        private Thread _thread;
        private volatile bool _isRunning;
        private long _renderTicks;
        private long _renderedFrames;
        private long _lastSkips;

        private Vfs _vfs;
        private Loader _loader;
        private Performance _performance;
        private AudioStreamPlayer _player;
        private AudioStreamGeneratorPlayback _playback;
        private IZoneMusic _defaultZone;
        private IZoneMusic _currentZone;
        private MusicThemeInstance _currentTheme;
        private Func<Vector3> _listener;
        private bool _isNight;
        private bool _isRenderingDisabled;
        private double _zoneTime;
        private double _logTime;

        public string CurrentTheme => _currentTheme?.File;
        public string CurrentZone => _currentZone?.Name ?? _defaultZone?.Name;

        /// <param name="listener">Where the hero is (the camera or the player's feet).</param>
        /// <param name="hour">Day from 4:00 to 21:00 like Gothic-Unity, else night.</param>
        public void Init(Vfs vfs, ZenKit.World world, Func<Vector3> listener, int hour, float volume)
        {
            _vfs = vfs;
            _listener = listener;
            _isNight = hour < 4 || hour > 21;

            var vm = new DaedalusVm(vfs, "MUSIC.DAT");
            foreach (var symbol in vm.GetInstanceSymbols("C_MUSICTHEME"))
                _themes[symbol.Name] = vm.InitInstance<MusicThemeInstance>(symbol);

            foreach (var vob in world.RootObjects)
                CollectZones(vob);

            // No LoaderOptions.Download: dmusic's own download error path double-frees the segment (Gothic-Unity
            // finding); Segment.Download is called by hand where a failure is a managed exception.
            _loader = Loader.Create(LoaderOptions.Default);
            _loader.AddResolver(name => _vfs.Find(name)?.Buffer.Bytes);
            _performance = Performance.Create(_mixRate);

            _player = new AudioStreamPlayer
            {
                Name = "Music",
                Stream = new AudioStreamGenerator { MixRate = _mixRate, BufferLength = _bufferSeconds },
                VolumeDb = Mathf.LinearToDb(volume)
            };
            AddChild(_player);
            _player.Play();
            _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();

            _currentZone = FindZone(_listener());
            _isRunning = true;
            _thread = new Thread(RenderLoop) { Name = "GothicMusic", IsBackground = true };
            _thread.Start();
            GD.Print($"[Music] {_themes.Count} themes, {_zones.Count} zones, default {_defaultZone?.Name ?? "-"}, " +
                     $"{(_isNight ? "night" : "day")}");
            if (OS.IsStdOutVerbose() || OS.GetCmdlineUserArgs().Contains("--music-zones"))
                GD.Print("[Music] zones: " + string.Join(", ", _zones.Select(z => $"{z.Zone.Name} {z.Box.GetCenter():F0}")));
            Play();
        }

        private void CollectZones(IVirtualObject vob)
        {
            if (vob is IZoneMusic zone)
            {
                if (vob is IZoneMusicDefault)
                    _defaultZone = zone;
                else
                    _zones.Add((zone, ToGodot(zone.BoundingBox)));
            }

            foreach (var child in vob.Children)
                CollectZones(child);
        }

        private static Aabb ToGodot(AxisAlignedBoundingBox box)
        {
            var a = MeshPart.ToGodot(box.Min);
            var b = MeshPart.ToGodot(box.Max);
            var min = new Vector3(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Min(a.Z, b.Z));
            var max = new Vector3(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y), Mathf.Max(a.Z, b.Z));
            return new Aabb(min, max - min);
        }

        public override void _Process(double delta)
        {
            if (_playback == null)
                return;

            _zoneTime += delta;
            if (_zoneTime >= _zoneCheckSeconds)
            {
                _zoneTime = 0;
                var zone = FindZone(_listener());
                if (zone != _currentZone)
                {
                    GD.Print($"[Music] zone {_currentZone?.Name ?? "-"} -> {zone?.Name ?? _defaultZone?.Name ?? "-"}");
                    _currentZone = zone;
                    Play();
                }
            }

            LogTimes(delta);
        }

        public override void _ExitTree()
        {
            _isRunning = false;
            _thread?.Join(500);
        }

        private void RenderLoop()
        {
            while (_isRunning)
            {
                if (!Render())
                    Thread.Sleep(4);
            }
        }

        /// <summary>Fills the generator: whole chunks of dmusic PCM while there is room. Runs on the music thread.</summary>
        private bool Render()
        {
            if (_playback.GetFramesAvailable() < _chunkFrames)
                return false;

            _renderWatch.Restart();
            while (_playback.GetFramesAvailable() >= _chunkFrames)
            {
                if (_isRenderingDisabled)
                {
                    Array.Clear(_frames);
                }
                else
                {
                    try
                    {
                        _performance.RenderPcm(_pcm, true);
                    }
                    catch (Exception e)
                    {
                        // Like Gothic-Unity: a faulted performance isn't touched again, silence instead.
                        _isRenderingDisabled = true;
                        GD.PushError($"[Music] RenderPcm failed ({e.Message}), music off");
                        Array.Clear(_pcm);
                    }

                    for (var i = 0; i < _chunkFrames; i++)
                        _frames[i] = new Vector2(_pcm[i * 2], _pcm[i * 2 + 1]);
                }

                _playback.PushBuffer(_frames);
                Interlocked.Add(ref _renderedFrames, _chunkFrames);
            }

            Interlocked.Add(ref _renderTicks, _renderWatch.ElapsedTicks);
            return true;
        }

        private void LogTimes(double delta)
        {
            _logTime += delta;
            if (_logTime < 5)
                return;
            var renderMs = Interlocked.Exchange(ref _renderTicks, 0) * 1000.0 / Stopwatch.Frequency;
            var frames = Interlocked.Exchange(ref _renderedFrames, 0);
            var skips = _playback.GetSkips();
            GD.Print($"[Music] {CurrentTheme ?? "-"} in {CurrentZone ?? "-"}: rendering {renderMs / _logTime:F2} ms " +
                     $"per second on the music thread ({frames / _logTime:F0} frames/s), underruns {skips - _lastSkips}");
            _lastSkips = skips;
            _logTime = 0;
        }

        /// <summary>Day (4:00-21:00) or night: the zone's other theme plays.</summary>
        public void SetHour(int hour)
        {
            var isNight = hour < 4 || hour > 21;
            if (isNight == _isNight)
                return;
            _isNight = isNight;
            GD.Print($"[Music] {(isNight ? "night" : "day")} falls");
            Play();
        }

        private IZoneMusic FindZone(Vector3 position)
        {
            if (_currentZone != null && _zones.Any(z => z.Zone == _currentZone && z.Box.HasPoint(position)))
                return _currentZone;

            IZoneMusic found = null;
            foreach (var (zone, box) in _zones)
                if (box.HasPoint(position))
                    found = zone;
            return found;
        }

        /// <summary>The zone's theme for the time of day, else the default zone's: ZONE_DAY_STD, ZONE_NGT_STD.</summary>
        private void Play()
        {
            var timeTags = _isNight ? new[] { "NGT", "DAY" } : new[] { "DAY" };
            foreach (var zone in new[] { _currentZone, _defaultZone }.Where(z => z != null))
            {
                // OLDCAMP_OC -> OC
                var name = zone.Name[(zone.Name.IndexOf('_') + 1)..];
                foreach (var time in timeTags)
                {
                    if (!_themes.TryGetValue($"{name}_{time}_STD", out var theme))
                        continue;
                    Play(theme);
                    return;
                }
            }
        }

        private void Play(MusicThemeInstance theme)
        {
            // Several instances share a file: no restart.
            if (_currentTheme != null && string.Equals(theme.File, _currentTheme.File, StringComparison.OrdinalIgnoreCase))
                return;

            var segment = GetSegment(theme.File);
            if (segment == null)
                return;

            _performance.PlayTransition(segment, ToEmbellishment(theme.TransType), ToTiming(theme.TransSubType));
            _currentTheme = theme;
            GD.Print($"[Music] theme {theme.File}");
        }

        private Segment GetSegment(string file)
        {
            if (_segments.TryGetValue(file, out var cached))
                return cached;
            if (_brokenSegments.Contains(file))
                return null;

            // dmusic crashes on some small unfinished themes (Gothic-Unity: under 1 kB).
            var node = _vfs.Find(file);
            if (node == null || node.Buffer.Bytes.Length < 1000)
            {
                GD.PushWarning($"[Music] theme {file} missing or too small, skipped");
                _brokenSegments.Add(file);
                return null;
            }

            Segment segment = null;
            try
            {
                segment = _loader.GetSegment(file);
                segment?.Download(_loader);
                if (segment != null)
                    _segments[file] = segment;
                return segment;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Music] theme {file} can't be loaded: {e.Message}");
                _brokenSegments.Add(file);
                // A failed download leaves the native segment half set up; its finalizer would free it twice.
                if (segment != null)
                    GC.SuppressFinalize(segment);
                return null;
            }
        }

        private static Timing ToTiming(MusicTransitionType type) => type switch
        {
            MusicTransitionType.Immediate => Timing.Instant,
            MusicTransitionType.Beat => Timing.Beat,
            _ => Timing.Measure
        };

        private static Embellishment ToEmbellishment(MusicTransitionEffect effect) => effect switch
        {
            // None or Unknown has to be End, else G1's normal transitions don't happen (Gothic-Unity).
            MusicTransitionEffect.Groove => Embellishment.Groove,
            MusicTransitionEffect.Fill => Embellishment.Fill,
            MusicTransitionEffect.Break => Embellishment.Break,
            MusicTransitionEffect.Intro => Embellishment.Intro,
            MusicTransitionEffect.EndAndInto => Embellishment.EndAndIntro,
            _ => Embellishment.End
        };
    }
}
