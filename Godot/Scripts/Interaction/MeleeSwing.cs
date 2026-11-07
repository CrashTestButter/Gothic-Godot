using System;
using System.Collections.Generic;
using Godot;
using GothicGodot.Player;
using GothicGodot.Spike;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// The hero's melee hits: XR - a melee weapon held in a hand hits the NPC whose hitbox its blade passes through
    /// faster than 2 m/s; desktop - a left click hits the NPC in front within 2.5 m. Damage and reactions are
    /// <see cref="GothicFight"/>'s.
    /// </summary>
    public partial class MeleeSwing : Node
    {
        private const float _swingSpeed = 2f;
        private const float _desktopReach = 2.5f;

        private GothicPlayer _player;
        private GothicAi _ai;
        private Func<bool> _isAllowed;
        private readonly Dictionary<string, bool> _isWeapon = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<WorldItem, BoxShape3D> _blades = new();
        private bool _wasClickDown;

        public event Action<GothicNpc, int> Hit;

        public void Init(GothicPlayer player, GothicAi ai, Func<bool> isAllowed)
        {
            _player = player;
            _ai = ai;
            _isAllowed = isAllowed;
        }

        private bool IsWeapon(WorldItem item)
        {
            if (item == null)
                return false;
            if (!_isWeapon.TryGetValue(item.Instance, out var isWeapon))
                _isWeapon[item.Instance] = isWeapon = _ai.Fight.IsMeleeWeapon(item.Instance);
            return isWeapon;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_player == null || !_player.IsPlaced || _isAllowed?.Invoke() == false)
                return;

            if (_player.IsXr)
            {
                CheckBlade(_player.LeftGrab);
                CheckBlade(_player.RightGrab);
                return;
            }

            var isDown = Input.IsMouseButtonPressed(MouseButton.Left);
            if (isDown && !_wasClickDown)
                DesktopSwing();
            _wasClickDown = isDown;
        }

        /// <summary>Desktop (and tests): the weapon in the right hand hits whoever is in front.</summary>
        public GothicNpc DesktopSwing()
        {
            var weapon = _player.RightGrab?.Held;
            if (!IsWeapon(weapon))
                return null;
            var camera = _player.Camera.GlobalTransform;
            var query = PhysicsRayQueryParameters3D.Create(camera.Origin, camera.Origin - camera.Basis.Z * _desktopReach,
                PhysicsLayers.Npcs | PhysicsLayers.World);
            query.CollideWithAreas = true;
            var hit = _player.GetWorld3D().DirectSpaceState.IntersectRay(query);
            var npc = hit.Count > 0 ? _ai.NpcOf(hit["collider"].AsGodotObject()) : null;
            if (npc != null)
                Strike(npc, weapon);
            return npc;
        }

        private void CheckBlade(GrabHand hand)
        {
            var weapon = hand?.Held;
            if (!IsWeapon(weapon) || hand.Velocity.Length() < _swingSpeed)
                return;
            var mesh = weapon.GetChildOrNull<MeshInstance3D>(0);
            if (mesh?.Mesh == null)
                return;
            if (!_blades.TryGetValue(weapon, out var blade))
                _blades[weapon] = blade = new BoxShape3D { Size = mesh.Mesh.GetAabb().Size };
            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = blade,
                Transform = weapon.GlobalTransform * new Transform3D(Basis.Identity, mesh.Mesh.GetAabb().GetCenter()),
                CollisionMask = PhysicsLayers.Npcs,
                CollideWithAreas = true,
                CollideWithBodies = false
            };
            foreach (var result in _player.GetWorld3D().DirectSpaceState.IntersectShape(query, 4))
            {
                var npc = _ai.NpcOf(result["collider"].AsGodotObject());
                if (npc != null)
                    Strike(npc, weapon);
            }
        }

        private void Strike(GothicNpc npc, WorldItem weapon)
        {
            var damage = _ai.Fight.HeroHits(npc, weapon.Instance, _ai.Time);
            if (damage >= 0)
                Hit?.Invoke(npc, damage);
        }
    }
}
