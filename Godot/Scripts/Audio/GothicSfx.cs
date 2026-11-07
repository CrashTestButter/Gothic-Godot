using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ZenKit;
using ZenKit.Daedalus;

namespace GothicGodot.Audio
{
    /// <summary>
    /// Gothic's sound effects (C_SFX in SFX.DAT): a name like FIRE_SMALL may have variants (FIRE_SMALL_A1, _A2, ...),
    /// one of them plays at random, at the instance's volume (0-127). Plays sounds for the scripts (Snd_Play,
    /// Snd_Play3d) from small pools of players; the world's sound objects use <see cref="Get"/>.
    /// </summary>
    public partial class GothicSfx : Node
    {
        private const int _pool2d = 6;
        private const int _pool3d = 12;

        private readonly Dictionary<string, List<string>> _variants = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SoundEffectInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AudioStreamPlayer> _players2d = new();
        private readonly List<AudioStreamPlayer3D> _players3d = new();
        private readonly System.Random _random = new();
        private DaedalusVm _vm;
        private Vfs _vfs;
        private int _next2d;
        private int _next3d;

        public int Count => _variants.Count;

        public void Init(Vfs vfs)
        {
            _vfs = vfs;
            _vm = new DaedalusVm(vfs, "SFX.DAT");
            var names = _vm.GetInstanceSymbols("C_SFX").Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                // FIRE_SMALL_A1 is a variant of FIRE_SMALL (when that exists, else a name of its own).
                var at = name.LastIndexOf("_A", StringComparison.OrdinalIgnoreCase);
                var key = at > 0 && int.TryParse(name[(at + 2)..], out _) && names.Contains(name[..at]) ? name[..at] : name;
                if (!_variants.TryGetValue(key, out var list))
                    _variants[key] = list = new List<string>();
                list.Add(name);
            }

            for (var i = 0; i < _pool2d; i++)
            {
                var player = new AudioStreamPlayer();
                AddChild(player);
                _players2d.Add(player);
            }

            for (var i = 0; i < _pool3d; i++)
            {
                var player = new AudioStreamPlayer3D { UnitSize = 5f, MaxDistance = 30f };
                AddChild(player);
                _players3d.Add(player);
            }
        }

        /// <summary>One of the sound's variants (or a .WAV file by name) with its volume (0-1); null if unknown.</summary>
        public AudioStreamWav Get(string name, out float volume)
        {
            volume = 1f;
            if (string.IsNullOrEmpty(name))
                return null;
            if (name.EndsWith(".WAV", StringComparison.OrdinalIgnoreCase))
                return GothicSound.Load(_vfs, name);
            if (!_variants.TryGetValue(name, out var variants))
                return null;

            var instance = GetInstance(variants[_random.Next(variants.Count)]);
            if (instance == null || string.IsNullOrEmpty(instance.File))
                return null;
            volume = Mathf.Clamp(instance.Volume / 127f, 0f, 1f);
            return GothicSound.Load(_vfs, instance.File);
        }

        private SoundEffectInstance GetInstance(string name)
        {
            if (_instances.TryGetValue(name, out var cached))
                return cached;
            SoundEffectInstance instance = null;
            try
            {
                instance = _vm.InitInstance<SoundEffectInstance>(name);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Sfx] {name} can't be read: {e.Message}");
            }

            _instances[name] = instance;
            return instance;
        }

        /// <summary>Snd_Play: for the hero's ears (lock clicks, the menu, a found item).</summary>
        public void Play(string name)
        {
            var stream = Get(name, out var volume);
            if (stream == null)
                return;
            var player = _players2d[_next2d++ % _players2d.Count];
            player.Stream = stream;
            player.VolumeDb = Mathf.LinearToDb(volume);
            player.Play();
        }

        /// <summary>A voice line at a place in the world (NPC talk), louder and further than effects.</summary>
        public void PlayVoice(AudioStreamWav stream, Vector3 position)
        {
            var player = _players3d[_next3d++ % _players3d.Count];
            player.Stream = stream;
            player.GlobalPosition = position;
            player.VolumeDb = 0;
            player.Play();
        }

        /// <summary>Snd_Play3d: at a place in the world (an NPC).</summary>
        public void Play3d(string name, Vector3 position)
        {
            var stream = Get(name, out var volume);
            if (stream == null)
                return;
            var player = _players3d[_next3d++ % _players3d.Count];
            player.Stream = stream;
            player.GlobalPosition = position;
            player.VolumeDb = Mathf.LinearToDb(volume);
            player.Play();
        }
    }
}
