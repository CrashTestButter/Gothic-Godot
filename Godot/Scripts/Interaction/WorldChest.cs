using System;
using System.Collections.Generic;
using Godot;
using Gothic.Core.Extensions;
using GothicGodot.Spike;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// A chest (oCMobContainer): its own animated model, closed (S_S0), opening (T_S0_2_S1), open (S_S1) and closing
    /// (T_S1_2_S0). The first opening lays its contents (the ZEN's "ITMINUGGET:20;ITFO_APPLE" list) inside as items the
    /// hands can take. Locked chests (key or lock combination) stay shut for now.
    /// </summary>
    public partial class WorldChest : Node3D
    {
        private ModelFactory _factory;
        private Skeleton3D _skeleton;
        private ModelFactory.Clip _clip;
        private double _clipTime;
        private bool _isClipLooping;
        private string _contents;
        private bool _isFilled;
        private Aabb _bounds;
        private Func<string, Transform3D, WorldItem> _createItem;
        private Node3D _itemParent;

        public string Model => _factory?.Model;
        public bool IsLocked { get; private set; }
        public bool IsOpen { get; private set; }
        public bool IsMoving => _clip != null && !_isClipLooping;
        public bool HasContents => !string.IsNullOrWhiteSpace(_contents);
        public List<WorldItem> Items { get; } = new();

        /// <summary>Where a hand opens it: the middle of the lid, so it works from any side.</summary>
        public Vector3 LidPoint => GlobalTransform * new Vector3(_bounds.GetCenter().X, _bounds.End.Y, _bounds.GetCenter().Z);

        public Vector3 Centre => GlobalTransform * _bounds.GetCenter();

        public bool Setup(ModelFactory factory, string contents, bool isLocked, Color tint,
            Func<string, Transform3D, WorldItem> createItem, Node3D itemParent)
        {
            _factory = factory;
            _contents = contents;
            _createItem = createItem;
            _itemParent = itemParent;
            IsLocked = isLocked;

            var mesh = factory.GetSkinnedMesh($"{factory.Model}.MDM") ?? factory.GetSkinnedMesh($"{factory.Model}.MDL");
            if (mesh == null)
                return false;
            _skeleton = factory.CreateSkeleton();
            AddChild(_skeleton);
            var instance = new MeshInstance3D { Mesh = mesh, Skin = factory.Skin };
            _skeleton.AddChild(instance);
            instance.Skeleton = instance.GetPathTo(_skeleton);
            instance.SetInstanceShaderParameter("tint", tint);

            // Solid like the merged chests were (cdStatic): a box around the closed chest.
            _bounds = mesh.GetAabb();
            var body = new StaticBody3D { CollisionLayer = PhysicsLayers.Mobs, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = _bounds.Size * new Vector3(1f, 0.9f, 1f) },
                Position = _bounds.GetCenter() - new Vector3(0, _bounds.Size.Y * 0.05f, 0)
            });
            AddChild(body);
            Play("S_S0", true);
            return true;
        }

        /// <summary>Opens a closed chest or closes an open one; false while it moves or when it is locked.</summary>
        public bool Toggle()
        {
            if (IsMoving)
                return false;
            if (IsLocked)
            {
                GD.Print($"[Chest] {Name} is locked");
                return false;
            }

            IsOpen = !IsOpen;
            if (!Play(IsOpen ? "T_S0_2_S1" : "T_S1_2_S0", false))
                Play(IsOpen ? "S_S1" : "S_S0", true);
            GD.Print($"[Chest] {Name} {(IsOpen ? "opens" : "closes")}");
            return true;
        }

        public override void _Process(double delta)
        {
            if (_clip == null)
                return;
            _clipTime += delta;
            _clip.Apply(_skeleton, _clipTime, _isClipLooping);
            if (_isClipLooping || _clipTime < _clip.Duration)
                return;

            // The lid is up or down: hold that state; the first time it is up, the contents are there.
            Play(IsOpen ? "S_S1" : "S_S0", true);
            if (IsOpen && !_isFilled)
                Fill();
        }

        private bool Play(string name, bool isLooping)
        {
            var clip = _factory.GetClip(name);
            if (clip == null)
                return false;
            _clip = clip;
            _clipTime = 0;
            _isClipLooping = isLooping;
            _clip.Apply(_skeleton, 0, isLooping);
            return true;
        }

        /// <summary>The contents on the chest's floor in rows, a little apart, each a frozen item.</summary>
        private void Fill()
        {
            _isFilled = true;
            var entries = _contents.UnpackItems();
            if (entries.Count == 0)
                return;

            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(entries.Count)));
            var inner = new Vector3(_bounds.Size.X * 0.6f, 0, _bounds.Size.Z * 0.5f);
            var floor = _bounds.Position.Y + _bounds.Size.Y * 0.35f;
            for (var i = 0; i < entries.Count; i++)
            {
                var column = i % columns;
                var row = i / columns;
                var rows = (entries.Count + columns - 1) / columns;
                var local = new Vector3(
                    _bounds.GetCenter().X - inner.X * 0.5f + inner.X * (column + 0.5f) / columns,
                    floor,
                    _bounds.GetCenter().Z - inner.Z * 0.5f + inner.Z * (row + 0.5f) / rows);
                var item = _createItem?.Invoke(entries[i].Name.Trim(), new Transform3D(GlobalBasis, GlobalTransform * local));
                if (item == null)
                    continue;
                item.Amount = Math.Max(1, entries[i].Amount);
                _itemParent.AddChild(item);
                Items.Add(item);
            }

            GD.Print($"[Chest] {Name}: {Items.Count} of {entries.Count} entries laid out ({_contents})");
        }
    }
}
