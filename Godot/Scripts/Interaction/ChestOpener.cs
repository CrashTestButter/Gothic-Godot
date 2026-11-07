using System;
using System.Collections.Generic;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Opens and closes chests: XR - the grip with an empty hand on the lid (no item in reach);
    /// desktop - F at a chest in front within 2.2 m (when no NPC to talk to is in front).
    /// </summary>
    public partial class ChestOpener : Node
    {
        private const float _lidReach = 0.45f;
        private const float _desktopReach = 2.2f;

        private GothicPlayer _player;
        private IReadOnlyList<WorldChest> _chests;
        private Func<bool> _isAllowed;
        private readonly bool[] _wasGripDown = new bool[2];
        private bool _wasKeyDown;

        public void Init(GothicPlayer player, IReadOnlyList<WorldChest> chests, Func<bool> isAllowed)
        {
            _player = player;
            _chests = chests;
            _isAllowed = isAllowed;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_player == null || !_player.IsPlaced || _isAllowed?.Invoke() == false)
                return;

            if (_player.IsXr)
            {
                CheckHand(_player.LeftHand, _player.LeftGrab, 0);
                CheckHand(_player.RightHand, _player.RightGrab, 1);
                return;
            }

            var isDown = Input.IsKeyPressed(Key.F);
            if (isDown && !_wasKeyDown)
                FindInView()?.Toggle();
            _wasKeyDown = isDown;
        }

        private void CheckHand(XRController3D controller, GrabHand hand, int slot)
        {
            if (controller == null || hand == null)
                return;
            var isDown = controller.GetFloat("grip") > 0.6f || controller.IsButtonPressed("grip_click");
            var isPressed = isDown && !_wasGripDown[slot];
            _wasGripDown[slot] = isDown;
            if (!isPressed || hand.Held != null || hand.IsItemInReach())
                return;

            WorldChest nearest = null;
            var best = _lidReach;
            foreach (var chest in _chests)
            {
                var distance = chest.LidPoint.DistanceTo(hand.GlobalPosition);
                if (distance >= best)
                    continue;
                nearest = chest;
                best = distance;
            }

            nearest?.Toggle();
        }

        /// <summary>The nearest chest in front of the eyes (within about 40° of the view).</summary>
        public WorldChest FindInView()
        {
            var camera = _player.Camera.GlobalTransform;
            var forward = -camera.Basis.Z.Normalized();
            WorldChest nearest = null;
            var best = _desktopReach;
            foreach (var chest in _chests)
            {
                var toChest = chest.Centre - camera.Origin;
                var distance = toChest.Length();
                if (distance >= best || distance < 0.01f || toChest.Normalized().Dot(forward) < 0.75f)
                    continue;
                nearest = chest;
                best = distance;
            }

            return nearest;
        }
    }
}
