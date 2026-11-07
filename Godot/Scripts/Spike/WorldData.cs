using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using ZenKit.Vobs;

namespace GothicGodot.Spike
{
    /// <summary>
    /// What the game takes from the world file besides its geometry, collected in one walk over its objects:
    /// waynet, freepoints, items, chests and doors, particle effects, sound objects, music zones. Saved with the
    /// world cache, so a cached start doesn't read the world file at all (G2: 0.6 s of 1.5 s on the desktop went to
    /// reading it and walking its 12k objects four times).
    /// </summary>
    public class WorldData
    {
        private const int _format = 1;

        public class Mob
        {
            public string Name;
            public string Model;
            public Transform3D Transform;
            public bool IsDoor;
            public bool IsLocked;
            public string Key;
            public string PickString;
            public string Contents;
        }

        public class Sound
        {
            public Vector3 Position;
            public float Radius;
            public string Name;
            public string DaytimeName;
            public bool IsDaytime;
            public float StartTime;
            public float EndTime;
            public int Mode;
            public float Volume;
            public float Delay;
            public float DelayVar;
        }

        public readonly List<(string Name, Vector3 Position, Vector3 Direction)> Waypoints = new();
        public readonly List<(string A, string B)> Edges = new();
        public readonly List<(string Name, Vector3 Position, Vector3 Direction)> FreePoints = new();
        public readonly List<(string Instance, string Visual, Transform3D Transform)> Items = new();
        public readonly List<Mob> Mobs = new();
        public readonly List<(string Visual, Transform3D Transform)> Effects = new();
        public readonly List<Sound> Sounds = new();
        public readonly List<(string Name, Aabb Box)> MusicZones = new();
        public string DefaultMusicZone;

        public static WorldData Collect(ZenKit.World world)
        {
            var data = new WorldData();
            foreach (var point in world.WayNet.Points)
                data.Waypoints.Add((point.Name, MeshPart.ToGodot(point.Position), MeshPart.ToGodot(point.Direction)));
            foreach (var edge in world.WayNet.Edges)
                data.Edges.Add((edge.A.Name, edge.B.Name));
            foreach (var root in world.RootObjects)
                data.Walk(root);
            return data;
        }

        private void Walk(IVirtualObject vob)
        {
            var visual = vob.Visual?.Name;
            switch (vob)
            {
                case IItem item:
                    // The visual comes from the item's script instance (C_ITEM.visual), resolved when it is built.
                    Items.Add((item.Instance, visual, TransformOf(vob)));
                    break;
                case IContainer or IDoor when visual?.EndsWith(".MDS", StringComparison.OrdinalIgnoreCase) == true:
                    var mob = new Mob
                    {
                        Name = vob.Name, Model = Path.GetFileNameWithoutExtension(visual).ToUpperInvariant(),
                        Transform = TransformOf(vob), IsDoor = vob is IDoor
                    };
                    if (vob is IContainer container)
                        (mob.IsLocked, mob.Key, mob.PickString, mob.Contents) =
                            (container.IsLocked, container.Key, container.PickString, container.Contents ?? "");
                    else if (vob is IDoor door)
                        (mob.IsLocked, mob.Key, mob.PickString, mob.Contents) =
                            (door.IsLocked, door.Key, door.PickString, "");
                    Mobs.Add(mob);
                    break;
                case ISound sound:
                    Sounds.Add(new Sound
                    {
                        Position = MeshPart.ToGodot(sound.Position), Radius = sound.Radius, Name = sound.SoundName,
                        DaytimeName = (sound as ISoundDaytime)?.SoundNameDaytime, IsDaytime = sound is ISoundDaytime,
                        StartTime = (sound as ISoundDaytime)?.StartTime ?? 0, EndTime = (sound as ISoundDaytime)?.EndTime ?? 0,
                        Mode = (int)sound.Mode, Volume = sound.Volume, Delay = sound.RandomDelay,
                        DelayVar = sound.RandomDelayVar
                    });
                    break;
                case IZoneMusicDefault defaultZone:
                    DefaultMusicZone = defaultZone.Name;
                    break;
                case IZoneMusic zone:
                    MusicZones.Add((zone.Name, ToGodot(zone.BoundingBox)));
                    break;
            }

            if (vob.Type == VirtualObjectType.zCVobSpot && !string.IsNullOrEmpty(vob.Name))
                // Gothic's forward (+Z) is -Z here, like the models.
                FreePoints.Add((vob.Name, MeshPart.ToGodot(vob.Position), -VobBuilder.ToGodot(vob.Rotation).Z.Normalized()));
            if (vob.ShowVisual && visual != null && visual.EndsWith(".PFX", StringComparison.OrdinalIgnoreCase))
                Effects.Add((visual, TransformOf(vob)));

            foreach (var child in vob.Children)
                Walk(child);
        }

        private static Transform3D TransformOf(IVirtualObject vob) =>
            new(VobBuilder.ToGodot(vob.Rotation), WorldChunkBuilder.ToGodot(vob.Position));

        private static Aabb ToGodot(ZenKit.AxisAlignedBoundingBox box)
        {
            var a = MeshPart.ToGodot(box.Min);
            var b = MeshPart.ToGodot(box.Max);
            var min = new Vector3(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Min(a.Z, b.Z));
            var max = new Vector3(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y), Mathf.Max(a.Z, b.Z));
            return new Aabb(min, max - min);
        }

        public void Save(string path)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(_format);
            w.Write(Waypoints.Count);
            foreach (var (name, position, direction) in Waypoints)
            {
                w.Write(name ?? "");
                Write(w, position);
                Write(w, direction);
            }

            w.Write(Edges.Count);
            foreach (var (a, b) in Edges)
            {
                w.Write(a ?? "");
                w.Write(b ?? "");
            }

            w.Write(FreePoints.Count);
            foreach (var (name, position, direction) in FreePoints)
            {
                w.Write(name);
                Write(w, position);
                Write(w, direction);
            }

            w.Write(Items.Count);
            foreach (var (instance, visual, transform) in Items)
            {
                w.Write(instance ?? "");
                w.Write(visual ?? "");
                Write(w, transform);
            }

            w.Write(Mobs.Count);
            foreach (var mob in Mobs)
            {
                w.Write(mob.Name ?? "");
                w.Write(mob.Model);
                Write(w, mob.Transform);
                w.Write(mob.IsDoor);
                w.Write(mob.IsLocked);
                w.Write(mob.Key ?? "");
                w.Write(mob.PickString ?? "");
                w.Write(mob.Contents ?? "");
            }

            w.Write(Effects.Count);
            foreach (var (visual, transform) in Effects)
            {
                w.Write(visual);
                Write(w, transform);
            }

            w.Write(Sounds.Count);
            foreach (var sound in Sounds)
            {
                Write(w, sound.Position);
                w.Write(sound.Radius);
                w.Write(sound.Name ?? "");
                w.Write(sound.DaytimeName ?? "");
                w.Write(sound.IsDaytime);
                w.Write(sound.StartTime);
                w.Write(sound.EndTime);
                w.Write(sound.Mode);
                w.Write(sound.Volume);
                w.Write(sound.Delay);
                w.Write(sound.DelayVar);
            }

            w.Write(MusicZones.Count);
            foreach (var (name, box) in MusicZones)
            {
                w.Write(name ?? "");
                Write(w, box.Position);
                Write(w, box.Size);
            }

            w.Write(DefaultMusicZone ?? "");
        }

        /// <summary>The saved data, or null when it is from another format.</summary>
        public static WorldData Load(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != _format)
                return null;
            var data = new WorldData();
            for (var i = r.ReadInt32(); i > 0; i--)
                data.Waypoints.Add((r.ReadString(), ReadVector(r), ReadVector(r)));
            for (var i = r.ReadInt32(); i > 0; i--)
                data.Edges.Add((r.ReadString(), r.ReadString()));
            for (var i = r.ReadInt32(); i > 0; i--)
                data.FreePoints.Add((r.ReadString(), ReadVector(r), ReadVector(r)));
            for (var i = r.ReadInt32(); i > 0; i--)
            {
                var instance = r.ReadString();
                var visual = r.ReadString();
                data.Items.Add((instance, visual.Length == 0 ? null : visual, ReadTransform(r)));
            }

            for (var i = r.ReadInt32(); i > 0; i--)
                data.Mobs.Add(new Mob
                {
                    Name = r.ReadString(), Model = r.ReadString(), Transform = ReadTransform(r), IsDoor = r.ReadBoolean(),
                    IsLocked = r.ReadBoolean(), Key = r.ReadString(), PickString = r.ReadString(), Contents = r.ReadString()
                });
            for (var i = r.ReadInt32(); i > 0; i--)
                data.Effects.Add((r.ReadString(), ReadTransform(r)));
            for (var i = r.ReadInt32(); i > 0; i--)
                data.Sounds.Add(new Sound
                {
                    Position = ReadVector(r), Radius = r.ReadSingle(), Name = r.ReadString(), DaytimeName = r.ReadString(),
                    IsDaytime = r.ReadBoolean(), StartTime = r.ReadSingle(), EndTime = r.ReadSingle(), Mode = r.ReadInt32(),
                    Volume = r.ReadSingle(), Delay = r.ReadSingle(), DelayVar = r.ReadSingle()
                });
            for (var i = r.ReadInt32(); i > 0; i--)
                data.MusicZones.Add((r.ReadString(), new Aabb(ReadVector(r), ReadVector(r))));
            var defaultZone = r.ReadString();
            data.DefaultMusicZone = defaultZone.Length == 0 ? null : defaultZone;
            return data;
        }

        private static void Write(BinaryWriter w, Vector3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        private static void Write(BinaryWriter w, Transform3D t)
        {
            Write(w, t.Basis.X);
            Write(w, t.Basis.Y);
            Write(w, t.Basis.Z);
            Write(w, t.Origin);
        }

        private static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static Transform3D ReadTransform(BinaryReader r) =>
            new(new Basis(ReadVector(r), ReadVector(r), ReadVector(r)), ReadVector(r));
    }
}
