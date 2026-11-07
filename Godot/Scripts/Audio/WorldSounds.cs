using System;
using System.Collections.Generic;
using Godot;
using GothicGodot.Spike;
using ZenKit.Vobs;

namespace GothicGodot.Audio
{
    /// <summary>
    /// The world's sound objects (zCVobSound, zCVobSoundDaytime): looping (fires, water, wind), random (birds, the
    /// camp's noises, with a pause of RandomDelay +- RandomDelayVar seconds) or once. Only those whose radius the
    /// listener is in play; checked twice a second, so a few dozen players instead of hundreds.
    /// </summary>
    public partial class WorldSounds : Node3D
    {
        private const double _checkSeconds = 0.5;
        private const float _margin = 3f;

        private class Source
        {
            public Vector3 Position;
            public float Radius;
            public string Name;
            public SoundMode Mode;
            public float Volume;
            public float Delay;
            public float DelayVar;
            public AudioStreamPlayer3D Player;
            public double NextPlay;
            public bool HasPlayedOnce;
        }

        private readonly List<Source> _sources = new();
        private readonly System.Random _random = new(5);
        private GothicSfx _sfx;
        private Func<Vector3> _listener;
        private double _checkTime;
        private double _time;

        public int Count => _sources.Count;
        public int Active { get; private set; }

        public void Init(GothicSfx sfx, IEnumerable<IVirtualObject> roots, Func<Vector3> listener, int hour)
        {
            _sfx = sfx;
            _listener = listener;
            foreach (var root in roots)
                Collect(root, hour);
        }

        private void Collect(IVirtualObject vob, int hour)
        {
            // IsAllowedToRun is false in the saved worlds; the engine runs them all.
            if (vob is ISound sound)
            {
                var name = sound.SoundName;
                // Daytime sounds swap their sound between StartTime and EndTime (hours).
                if (sound is ISoundDaytime daytime && IsBetween(hour, daytime.StartTime, daytime.EndTime))
                    name = daytime.SoundNameDaytime;
                if (!string.IsNullOrEmpty(name))
                    _sources.Add(new Source
                    {
                        Position = MeshPart.ToGodot(sound.Position),
                        Radius = Mathf.Max(1f, sound.Radius / 100f),
                        Name = name,
                        Mode = sound.Mode,
                        Volume = Mathf.Clamp(sound.Volume / 100f, 0f, 1f),
                        Delay = sound.RandomDelay,
                        DelayVar = sound.RandomDelayVar
                    });
            }

            foreach (var child in vob.Children)
                Collect(child, hour);
        }

        private static bool IsBetween(int hour, float start, float end) =>
            start <= end ? hour >= start && hour < end : hour >= start || hour < end;

        public override void _Process(double delta)
        {
            if (_sfx == null)
                return;
            _time += delta;
            _checkTime += delta;
            if (_checkTime < _checkSeconds)
                return;
            _checkTime = 0;

            var listener = _listener();
            var active = 0;
            foreach (var source in _sources)
            {
                var distance = source.Position.DistanceTo(listener);
                if (distance > source.Radius + _margin)
                {
                    if (source.Player != null)
                        Release(source);
                    continue;
                }

                active++;
                if (source.Player == null && !Start(source))
                    continue;
                Update(source);
            }

            Active = active;
            _logTime += _checkSeconds;
            if (_logTime < 5)
                return;
            _logTime = 0;
            GD.Print($"[Sounds] {active} of {_sources.Count} sound objects in range");
        }

        private double _logTime;

        private bool Start(Source source)
        {
            var stream = _sfx.Get(source.Name, out var volume);
            if (stream == null)
                return false;
            if (source.Mode == SoundMode.Loop)
            {
                // Its own looping copy: the decoded stream is shared with one-shot uses.
                stream = (AudioStreamWav)stream.Duplicate();
                stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                stream.LoopBegin = 0;
                stream.LoopEnd = FrameCount(stream);
            }

            source.Player = new AudioStreamPlayer3D
            {
                Stream = stream,
                UnitSize = Mathf.Max(1f, source.Radius * 0.25f),
                MaxDistance = source.Radius,
                VolumeDb = Mathf.LinearToDb(Mathf.Max(0.01f, volume * source.Volume))
            };
            AddChild(source.Player);
            source.Player.GlobalPosition = source.Position;
            source.NextPlay = _time;
            return true;
        }

        private void Update(Source source)
        {
            switch (source.Mode)
            {
                case SoundMode.Loop:
                    if (!source.Player.Playing)
                        source.Player.Play();
                    break;
                case SoundMode.Once:
                    if (!source.HasPlayedOnce)
                    {
                        source.HasPlayedOnce = true;
                        source.Player.Play();
                    }

                    break;
                case SoundMode.Random:
                    if (source.Player.Playing || _time < source.NextPlay)
                        break;
                    // Another variant each time, then a pause.
                    var stream = _sfx.Get(source.Name, out _);
                    if (stream != null)
                        source.Player.Stream = stream;
                    source.Player.Play();
                    var pause = source.Delay + (float)(_random.NextDouble() * 2 - 1) * source.DelayVar;
                    source.NextPlay = _time + (source.Player.Stream?.GetLength() ?? 0) + Mathf.Max(0.5f, pause);
                    break;
            }
        }

        private static int FrameCount(AudioStreamWav stream)
        {
            var bytesPerFrame = (stream.Stereo ? 2 : 1) * (stream.Format == AudioStreamWav.FormatEnum.Format16Bits ? 2 : 1);
            return stream.Data.Length / bytesPerFrame;
        }

        private void Release(Source source)
        {
            source.Player.QueueFree();
            source.Player = null;
        }
    }
}
