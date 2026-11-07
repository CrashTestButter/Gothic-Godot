using System;
using System.Collections.Generic;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// The world's collision near the start goes in at load, the rest after it, a piece per frame, nearest to the
    /// camera first: Jolt builds a mesh shape when it enters the scene (G2: 878k triangles, 0.65 s of the desktop's
    /// cached load). Nobody needs collision far away in the first seconds - the player walks, items lie frozen.
    /// Pieces near the camera (after a teleport) go in at once, whatever the budget.
    /// </summary>
    public partial class CollisionStreamer : Node
    {
        public const string BoundsMeta = "bounds";
        public const string TrianglesMeta = "triangles";

        private const double _sortInterval = 1.0;
        private const float _urgent = 30f;

        private readonly List<(CollisionShape3D Shape, Aabb Bounds, int Triangles)> _pending = new();
        private readonly Func<Vector3> _eye;
        private readonly StaticBody3D _source;
        private Node3D _target;
        private double _sortTime = double.MaxValue;
        private double _seconds;
        private int _added;
        private int _urgentAdded;
        private double _addMs;
        private double _maxAddMs;

        private static CollisionStreamer _instance;

        public int PendingCount => _pending.Count;
        public int PendingTriangles { get; private set; }

        public CollisionStreamer()
        {
        }

        /// <summary>Takes the pieces further than <paramref name="near"/> out of the body, before it enters the scene.</summary>
        public CollisionStreamer(StaticBody3D body, Vector3 start, float near, Func<Vector3> eye)
        {
            _source = body;
            _eye = eye;
            foreach (var child in body.GetChildren())
            {
                if (child is not CollisionShape3D shape || !shape.HasMeta(BoundsMeta))
                    continue;
                var bounds = shape.GetMeta(BoundsMeta).AsAabb();
                if (Distance(bounds, start) <= near)
                    continue;
                body.RemoveChild(shape);
                var triangles = shape.GetMeta(TrianglesMeta).AsInt32();
                _pending.Add((shape, bounds, triangles));
                PendingTriangles += triangles;
            }
        }

        public override void _EnterTree() => _instance = this;

        public override void _ExitTree()
        {
            if (_instance == this)
                _instance = null;
        }

        /// <summary>
        /// Puts the pieces still missing around a point in now: a teleport, a test looking for a far spot. Ray queries
        /// right after see them.
        /// </summary>
        public static void LoadNear(Vector3 point, float radius = _urgent)
        {
            if (_instance == null)
                return;
            for (var i = _instance._pending.Count - 1; i >= 0; i--)
            {
                if (Distance(_instance._pending[i].Bounds, point) > radius)
                    continue;
                _instance.Add(i);
                _instance._urgentAdded++;
            }
        }

        public override void _Process(double delta)
        {
            if (_pending.Count == 0)
            {
                GD.Print($"[Collision] all of the world's collision in after {_seconds:F1} s ({_added} pieces, " +
                         $"{_urgentAdded} of them at once near the camera; {_addMs / Math.Max(1, _added):F2} ms a " +
                         $"piece, at most {_maxAddMs:F2} ms)");
                QueueFree();
                return;
            }

            _seconds += delta;
            var eye = _eye();
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (Distance(_pending[i].Bounds, eye) > _urgent)
                    continue;
                Add(i);
                _urgentAdded++;
            }

            _sortTime += delta;
            if (_sortTime >= _sortInterval)
            {
                // Nearest last: taken from the end.
                _sortTime = 0;
                _pending.Sort((a, b) => Distance(b.Bounds, eye).CompareTo(Distance(a.Bounds, eye)));
            }

            if (_pending.Count > 0)
                Add(_pending.Count - 1);
        }

        private void Add(int index)
        {
            var (piece, _, triangles) = _pending[index];
            _pending.RemoveAt(index);
            PendingTriangles -= triangles;
            if (_target == null)
            {
                _target = new Node3D { Name = "WorldCollisionLate" };
                _source.GetParent().AddChild(_target);
            }

            // A body of its own: adding to the big one would rebuild its compound shape of all pieces each time.
            var body = new StaticBody3D
            {
                Name = $"Piece{_added}", CollisionLayer = _source.CollisionLayer, CollisionMask = _source.CollisionMask
            };
            body.AddChild(piece);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _target.AddChild(body);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _addMs += ms;
            _maxAddMs = Math.Max(_maxAddMs, ms);
            _added++;
        }

        private static float Distance(Aabb bounds, Vector3 point) =>
            point.Clamp(bounds.Position, bounds.End).DistanceTo(point);
    }
}
