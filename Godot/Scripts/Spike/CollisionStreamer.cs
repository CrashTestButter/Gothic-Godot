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
    ///
    /// Late pieces go straight to the physics server, a static body each (adding to the big body would rebuild its
    /// compound shape of all pieces each time; a node per piece cost more than the piece). Ray queries report the
    /// world's collision body as their collider, like for the pieces in it.
    /// </summary>
    public partial class CollisionStreamer : Node
    {
        public const string BoundsMeta = "bounds";
        public const string TrianglesMeta = "triangles";

        private const double _sortInterval = 1.0;
        private const float _urgent = 30f;
        // The near check runs again once the camera moved this far.
        private const float _recheckDistance = 2f;

        private class Piece
        {
            public Shape3D Shape;
            public Aabb Bounds;
            public int Triangles;
            public float Distance;
        }

        private static CollisionStreamer _instance;

        private readonly List<Piece> _pending = new();
        private readonly List<(Rid Body, Shape3D Shape)> _added = new();
        private readonly Func<Vector3> _eye;
        private readonly uint _layer;
        private readonly uint _mask;
        private readonly ulong _collider;
        private Vector3 _lastCheck = new(float.MaxValue, 0, 0);
        private double _sortTime = double.MaxValue;
        private double _seconds;
        private int _urgentAdded;
        private double _addMs;
        private double _maxAddMs;

        public int PendingCount => _pending.Count;
        public int PendingTriangles { get; private set; }

        public CollisionStreamer()
        {
        }

        /// <summary>Takes the pieces further than <paramref name="near"/> out of the body, before it enters the scene.</summary>
        public CollisionStreamer(StaticBody3D body, Vector3 start, float near, Func<Vector3> eye)
        {
            _eye = eye;
            _layer = body.CollisionLayer;
            _mask = body.CollisionMask;
            _collider = body.GetInstanceId();
            foreach (var child in body.GetChildren())
            {
                if (child is not CollisionShape3D shape || !shape.HasMeta(BoundsMeta))
                    continue;
                var bounds = shape.GetMeta(BoundsMeta).AsAabb();
                if (Distance(bounds, start) <= near)
                    continue;
                var triangles = shape.GetMeta(TrianglesMeta).AsInt32();
                _pending.Add(new Piece { Shape = shape.Shape, Bounds = bounds, Triangles = triangles });
                PendingTriangles += triangles;
                body.RemoveChild(shape);
                shape.Free();
            }
        }

        public override void _EnterTree() => _instance = this;

        public override void _ExitTree()
        {
            if (_instance == this)
                _instance = null;
            foreach (var (body, _) in _added)
                PhysicsServer3D.FreeRid(body);
            _added.Clear();
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
                GD.Print($"[Collision] all of the world's collision in after {_seconds:F1} s ({_added.Count} pieces, " +
                         $"{_urgentAdded} of them at once near the camera; {_addMs / Math.Max(1, _added.Count):F2} ms a " +
                         $"piece, at most {_maxAddMs:F2} ms after the first)");
                SetProcess(false);
                return;
            }

            _seconds += delta;
            var eye = _eye();
            if (eye.DistanceSquaredTo(_lastCheck) > _recheckDistance * _recheckDistance)
            {
                _lastCheck = eye;
                LoadNear(eye);
            }

            _sortTime += delta;
            if (_sortTime >= _sortInterval)
            {
                // Nearest last: taken from the end.
                _sortTime = 0;
                foreach (var piece in _pending)
                    piece.Distance = Distance(piece.Bounds, eye);
                _pending.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            }

            if (_pending.Count > 0)
                Add(_pending.Count - 1);
        }

        private void Add(int index)
        {
            var piece = _pending[index];
            _pending.RemoveAt(index);
            PendingTriangles -= piece.Triangles;

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var body = PhysicsServer3D.BodyCreate();
            PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodyAddShape(body, piece.Shape.GetRid());
            PhysicsServer3D.BodySetCollisionLayer(body, _layer);
            PhysicsServer3D.BodySetCollisionMask(body, _mask);
            PhysicsServer3D.BodyAttachObjectInstanceId(body, _collider);
            PhysicsServer3D.BodySetSpace(body, GetViewport().World3D.Space);
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _addMs += ms;
            // The first one includes the first calls into the server.
            if (_added.Count > 0)
                _maxAddMs = Math.Max(_maxAddMs, ms);
            else
                GD.Print($"[Collision] first late piece in {ms:F2} ms");
            // The shape resource stays alive with the body.
            _added.Add((body, piece.Shape));
        }

        private static float Distance(Aabb bounds, Vector3 point) =>
            point.Clamp(bounds.Position, bounds.End).DistanceTo(point);
    }
}
