using System.Collections.Generic;
using Godot;

namespace GothicGodot.Player
{
    /// <summary>
    /// Teleport arc: a throw parabola from the aiming hand (or the eyes on the desktop), cast segment by segment
    /// against the world. A hit is a valid target when it is floor (within the slope limit), at most 1.5 m above the
    /// feet (Gothic-Unity's teleport rule) and the body fits there. Drawn as a line, green or red, with a ring.
    /// </summary>
    public partial class TeleportAimer : Node3D
    {
        private const float _speed = 7f;
        private const float _gravity = 9.81f;
        private const float _step = 0.04f;
        private const int _maxSteps = 60;
        private const float _maxRise = 1.5f;

        private readonly List<Vector3> _points = new();
        private MeshInstance3D _line;
        private MeshInstance3D _ring;
        private ImmediateMesh _mesh;
        private StandardMaterial3D _valid;
        private StandardMaterial3D _invalid;

        public bool HasTarget { get; private set; }
        public bool IsTargetValid { get; private set; }
        public Vector3 Target { get; private set; }

        public override void _Ready()
        {
            _mesh = new ImmediateMesh();
            _valid = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 1f, 0.4f) };
            _invalid = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.3f, 0.25f) };
            _line = new MeshInstance3D { Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true };
            AddChild(_line);
            _ring = new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = 0.22f, OuterRadius = 0.28f, Rings = 24 },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                TopLevel = true,
                Visible = false
            };
            AddChild(_ring);
            Hide();
        }

        /// <summary>Casts the arc from <paramref name="origin"/> along <paramref name="direction"/>.</summary>
        public void Aim(PhysicsDirectSpaceState3D space, Vector3 origin, Vector3 direction, Vector3 feet,
            float maxSlopeRadians, CharacterBody3D body)
        {
            _points.Clear();
            HasTarget = false;
            IsTargetValid = false;
            var velocity = direction.Normalized() * _speed;
            var position = origin;
            _points.Add(position);
            var exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

            for (var i = 0; i < _maxSteps; i++)
            {
                var next = position + velocity * _step;
                velocity.Y -= _gravity * _step;
                var query = PhysicsRayQueryParameters3D.Create(position, next);
                query.Exclude = exclude;
                var hit = space.IntersectRay(query);
                if (hit.Count > 0)
                {
                    var point = (Vector3)hit["position"];
                    var normal = (Vector3)hit["normal"];
                    _points.Add(point);
                    HasTarget = true;
                    Target = point;
                    IsTargetValid = normal.AngleTo(Vector3.Up) <= maxSlopeRadians && point.Y - feet.Y <= _maxRise &&
                                    Fits(body, point);
                    break;
                }

                _points.Add(next);
                position = next;
            }

            Draw();
        }

        private static bool Fits(CharacterBody3D body, Vector3 point)
        {
            // Start a little above the target and see whether the body can sit down there.
            var transform = body.GlobalTransform;
            transform.Origin = point + Vector3.Up * 0.1f;
            return !body.TestMove(transform, Vector3.Up * 0.05f);
        }

        private void Draw()
        {
            Show();
            var material = IsTargetValid ? _valid : _invalid;
            _mesh.ClearSurfaces();
            _mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, material);
            foreach (var point in _points)
                _mesh.SurfaceAddVertex(point);
            _mesh.SurfaceEnd();
            _line.GlobalTransform = Transform3D.Identity;

            _ring.Visible = HasTarget;
            if (HasTarget)
            {
                _ring.MaterialOverride = material;
                _ring.GlobalPosition = Target + Vector3.Up * 0.02f;
            }
        }

        public void Stop()
        {
            HasTarget = false;
            IsTargetValid = false;
            Hide();
        }
    }
}
