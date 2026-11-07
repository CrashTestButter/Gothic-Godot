using System;
using Godot;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// A hand that takes items: XR - the grip takes the nearest item within reach of the hand and keeps it where it
    /// was touched; desktop - the right mouse button takes the item under the crosshair (2.5 m) into the hand in front
    /// of the camera. Letting go throws the item with the hand's velocity (averaged over a few physics frames).
    /// </summary>
    public partial class GrabHand : Node3D
    {
        private const float _reachRadius = 0.12f;
        private const float _desktopReach = 2.5f;
        private const float _aimWidth = 0.4f;
        private const float _pastSurface = 0.5f;
        private const int _velocityFrames = 4;

        private readonly Vector3[] _velocities = new Vector3[_velocityFrames];
        private readonly Vector3[] _angularVelocities = new Vector3[_velocityFrames];
        private readonly SphereShape3D _reach = new() { Radius = _reachRadius };
        private int _velocityIndex;
        private Transform3D _lastTransform;
        private bool _hasLastTransform;
        private bool _wasGripDown;
        private Transform3D _offset;

        /// <summary>Whether the grip (XR) or the grab button (desktop) is held.</summary>
        public Func<bool> IsGripDown;

        /// <summary>Desktop: items are picked along this camera's view instead of at the hand.</summary>
        public Camera3D AimCamera;

        public WorldItem Held { get; private set; }

        /// <summary>For tests: grip held or not, instead of the input.</summary>
        public bool? ForcedGrip;

        public event Action<WorldItem> Grabbed;
        public event Action<WorldItem, Vector3> Released;

        public override void _PhysicsProcess(double delta)
        {
            TrackVelocity((float)delta);

            var isDown = ForcedGrip ?? IsGripDown?.Invoke() ?? false;
            if (isDown && !_wasGripDown)
                TryGrab();
            else if (!isDown && _wasGripDown)
                Release();
            _wasGripDown = isDown;

            if (Held != null)
                Held.GlobalTransform = GlobalTransform * _offset;
        }

        private void TrackVelocity(float delta)
        {
            var transform = GlobalTransform;
            if (_hasLastTransform && delta > 0)
            {
                _velocities[_velocityIndex] = (transform.Origin - _lastTransform.Origin) / delta;
                var turn = new Quaternion(transform.Basis.Orthonormalized()) *
                           new Quaternion(_lastTransform.Basis.Orthonormalized()).Inverse();
                var angle = turn.GetAngle();
                if (angle > Mathf.Pi)
                    angle -= Mathf.Tau;
                _angularVelocities[_velocityIndex] = angle > 0.0001f || angle < -0.0001f
                    ? turn.GetAxis() * angle / delta
                    : Vector3.Zero;
                _velocityIndex = (_velocityIndex + 1) % _velocityFrames;
            }

            _lastTransform = transform;
            _hasLastTransform = true;
        }

        private void TryGrab()
        {
            var item = AimCamera != null ? FindAimed() : FindNearest();
            if (item == null || item.IsHeld)
                return;

            Held = item;
            item.Take();
            // XR: the item stays where the hand touched it. Desktop: it comes into the hand, keeping its turn.
            _offset = AimCamera != null
                ? new Transform3D(GlobalBasis.Inverse() * item.GlobalBasis, Vector3.Zero)
                : GlobalTransform.AffineInverse() * item.GlobalTransform;
            GD.Print($"[Grab] {Name} takes {item.Instance}");
            Grabbed?.Invoke(item);
        }

        /// <summary>Puts the item into this hand (taken out of the inventory): at the grip point, turned like the hand.</summary>
        public void Hold(WorldItem item)
        {
            if (Held != null)
                return;
            Held = item;
            item.Take();
            item.GlobalTransform = GlobalTransform;
            _offset = Transform3D.Identity;
            _wasGripDown = ForcedGrip ?? IsGripDown?.Invoke() ?? false;
            Grabbed?.Invoke(item);
        }

        /// <summary>Empties the hand without throwing (the item goes into the inventory).</summary>
        public WorldItem TakeAway()
        {
            var item = Held;
            Held = null;
            return item;
        }

        private void Release()
        {
            if (Held == null)
                return;
            var velocity = Average(_velocities);
            var item = Held;
            Held = null;
            item.Release(velocity, Average(_angularVelocities));
            GD.Print($"[Grab] {Name} lets go of {item.Instance} at {velocity.Length():F1} m/s");
            Released?.Invoke(item, velocity);
        }

        private static Vector3 Average(Vector3[] values)
        {
            var sum = Vector3.Zero;
            foreach (var value in values)
                sum += value;
            return sum / values.Length;
        }

        /// <summary>XR: an item is within reach of the hand (the grip would take it).</summary>
        public bool IsItemInReach() => Held == null && FindNearest() != null;

        private WorldItem FindNearest()
        {
            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = _reach,
                Transform = new Transform3D(Basis.Identity, GlobalPosition),
                CollisionMask = PhysicsLayers.Items
            };
            WorldItem best = null;
            var bestDistance = float.MaxValue;
            foreach (var hit in GetWorld3D().DirectSpaceState.IntersectShape(query, 8))
            {
                if (hit["collider"].As<GodotObject>() is not WorldItem item || item.IsHeld)
                    continue;
                var distance = item.GlobalPosition.DistanceSquaredTo(GlobalPosition);
                if (distance >= bestDistance)
                    continue;
                best = item;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>
        /// The item closest to the view line within reach, in front of the first wall: thin items (swords, plants)
        /// are easy to miss with a single ray.
        /// </summary>
        private WorldItem FindAimed()
        {
            var space = GetWorld3D().DirectSpaceState;
            var from = AimCamera.GlobalPosition;
            var forward = -AimCamera.GlobalBasis.Z;
            var wall = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + forward * _desktopReach,
                PhysicsLayers.World));
            // Gothic's items often sink into the table or shelf they lie on: a little past the first surface still counts.
            var reach = wall.Count > 0
                ? Mathf.Min(_desktopReach, from.DistanceTo(wall["position"].AsVector3()) + _pastSurface)
                : _desktopReach;

            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = new BoxShape3D { Size = new Vector3(_aimWidth, _aimWidth, reach) },
                Transform = new Transform3D(AimCamera.GlobalBasis, from + forward * (reach * 0.5f)),
                CollisionMask = PhysicsLayers.Items
            };
            WorldItem best = null;
            var bestDistance = float.MaxValue;
            var hits = space.IntersectShape(query, 16);
            foreach (var hit in hits)
            {
                if (hit["collider"].As<GodotObject>() is not WorldItem item || item.IsHeld)
                    continue;
                var toItem = item.GlobalPosition - from;
                var distance = (toItem - forward * toItem.Dot(forward)).Length();
                if (distance >= bestDistance)
                    continue;
                best = item;
                bestDistance = distance;
            }

            return best;
        }
    }
}
