using System;
using System.Collections.Generic;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Things made only when the camera comes near: items and particle emitters are drawn up to a few dozen metres,
    /// but G2 has 2311 items and 666 emitters - made at load they cost 0.45 s and stay thousands of nodes and frozen
    /// bodies far from anyone. Those near the start are made at load, the others nearest first, a few per frame, once
    /// the camera is within the radius. Made once, never taken away again (a moved item stays where it was put).
    /// </summary>
    public partial class NearSpawner : Node
    {
        private const float _rescanDistance = 1f;

        private class Entry
        {
            public Vector3 Position;
            public Action Create;
            public float Distance;
        }

        private static readonly List<NearSpawner> _instances = new();

        private readonly List<Entry> _pending = new();
        private readonly List<Entry> _due = new();
        private readonly string _what;
        private readonly Func<Vector3> _eye;
        private readonly float _radius;
        private readonly int _perFrame;
        private Vector3 _lastScan = new(float.MaxValue, 0, 0);
        private int _made;

        public int PendingCount => _pending.Count + _due.Count;

        public NearSpawner()
        {
        }

        /// <param name="what">For the log.</param>
        /// <param name="radius">Made once the camera is this near (metres).</param>
        /// <param name="perFrame">At most this many a frame (each is a few nodes).</param>
        public NearSpawner(string what, Func<Vector3> eye, float radius, int perFrame)
        {
            _what = what;
            _eye = eye;
            _radius = radius;
            _perFrame = perFrame;
        }

        /// <summary>
        /// At load only what is this near the start is made at once (the first view); the rest within the radius
        /// follows nearest first in the first frames.
        /// </summary>
        public const float AtLoad = 20f;

        /// <summary>Made now if near <paramref name="start"/>, else later.</summary>
        public void Add(Vector3 position, Vector3 start, Action create)
        {
            var distance = position.DistanceTo(start);
            if (distance <= AtLoad)
            {
                create();
                _made++;
                return;
            }

            _pending.Add(new Entry { Position = position, Create = create, Distance = distance });
        }

        public override void _EnterTree() => _instances.Add(this);

        public override void _ExitTree() => _instances.Remove(this);

        /// <summary>Makes everything waiting around a point now, in all spawners (a test looking for a far item).</summary>
        public static void MakeNear(Vector3 point, float radius)
        {
            foreach (var spawner in _instances)
                spawner.Make(point, radius);
        }

        private void Make(Vector3 point, float radius)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Position.DistanceTo(point) > radius)
                    continue;
                _pending[i].Create();
                _made++;
                _pending.RemoveAt(i);
            }
        }

        public override void _Process(double delta)
        {
            var eye = _eye();
            if (eye.DistanceSquaredTo(_lastScan) > _rescanDistance * _rescanDistance)
            {
                _lastScan = eye;
                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    var entry = _pending[i];
                    entry.Distance = entry.Position.DistanceTo(eye);
                    if (entry.Distance > _radius)
                        continue;
                    _due.Add(entry);
                    _pending.RemoveAt(i);
                }

                // Nearest last: taken from the end.
                _due.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            }

            for (var n = 0; n < _perFrame && _due.Count > 0; n++)
            {
                _due[^1].Create();
                _due.RemoveAt(_due.Count - 1);
                _made++;
            }
        }

        public override string ToString() => $"{_what}: {_made} made, {PendingCount} when the camera comes within {_radius} m";
    }
}
