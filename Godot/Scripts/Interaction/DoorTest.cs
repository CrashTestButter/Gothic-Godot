using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Scripted door test without a headset (--door-test): the desktop player stands in front of the nearest unlocked
    /// door, walks into it (blocked), opens it (checks the desktop pick finds it) and walks through. Screenshot of the
    /// open door, then quits.
    /// </summary>
    public partial class DoorTest : Node
    {
        private const float _walkSeconds = 2f;

        private GothicPlayer _player;
        private IReadOnlyList<WorldMob> _mobs;
        private string _screenshot;
        private double _time;
        private int _step;
        private WorldMob _door;
        private Vector3 _start;
        private Vector3 _through;
        private float _blocked;

        public void Init(GothicPlayer player, IReadOnlyList<WorldMob> mobs, string screenshot)
        {
            _player = player;
            _mobs = mobs;
            _screenshot = screenshot;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_player == null || !_player.IsPlaced)
                return;
            _time += delta;
            switch (_step)
            {
                case 0 when _time > 2:
                    var spot = Vector3.Zero;
                    _door = _mobs.Where(m => m.IsDoor && !m.IsLocked)
                        .OrderBy(m => m.Centre.DistanceSquaredTo(_player.GlobalPosition))
                        .FirstOrDefault(m => TryFindSpot(m, out spot));
                    if (_door == null)
                    {
                        Finish("no reachable unlocked door", 1);
                        return;
                    }

                    _start = spot;
                    _player.PlaceAt(spot, 0);
                    GD.Print($"[DoorTest] {_door.Name} ({_door.Model}) at {_door.Centre}, standing at {spot}");
                    Next();
                    break;
                case 1 when _time > 1:
                    _player.LookAtPoint(_door.Centre);
                    _start = _player.GlobalPosition;
                    _player.ForcedMove = new Vector2(0, -1);
                    Next();
                    break;
                case 2 when _time > _walkSeconds:
                    _player.ForcedMove = null;
                    _blocked = Flat(_player.GlobalPosition - _start).Length();
                    GD.Print($"[DoorTest] walked {_blocked:F2} m towards the closed door");
                    _player.LookAtPoint(_door.Centre);
                    var opener = GetParent().GetNodeOrNull<MobOpener>("MobOpener");
                    if (opener?.FindInView() != _door)
                    {
                        Finish($"the mob in view is {opener?.FindInView()?.Name ?? "none"}", 1);
                        return;
                    }

                    _door.Toggle();
                    Next();
                    break;
                case 3 when _time > 2:
                    Screenshot("open");
                    _start = _player.GlobalPosition;
                    _player.LookAtPoint(_door.Centre);
                    _player.ForcedMove = new Vector2(0, -1);
                    Next();
                    break;
                case 4 when _time > _walkSeconds:
                    _player.ForcedMove = null;
                    _through = _player.GlobalPosition;
                    var walked = Flat(_through - _start).Length();
                    GD.Print($"[DoorTest] walked {walked:F2} m through the open door (closed: {_blocked:F2} m)");
                    Finish(walked > _blocked + 0.8f ? "done" : "the open door still blocks", walked > _blocked + 0.8f ? 0 : 1);
                    break;
            }
        }

        private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

        /// <summary>A floor about 1.2 m in front of or behind the door, at its foot, with a view of its middle.</summary>
        private bool TryFindSpot(WorldMob door, out Vector3 spot)
        {
            var space = _player.GetWorld3D().DirectSpaceState;
            var target = door.Centre;
            var across = door.GlobalBasis.Z.Normalized();
            foreach (var side in new[] { 1f, -1f })
            {
                var around = target + across * (1.2f * side);
                var floor = space.IntersectRay(PhysicsRayQueryParameters3D.Create(around + Vector3.Up * 0.5f,
                    around + Vector3.Down * 2.5f, PhysicsLayers.World));
                if (floor.Count == 0)
                    continue;
                spot = floor["position"].AsVector3();
                if (target.Y - spot.Y is < 0.5f or > 1.8f)
                    continue;
                var eyes = spot + Vector3.Up * 1.6f;
                var view = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eyes, target, PhysicsLayers.World));
                if (view.Count == 0)
                    return true;
            }

            spot = Vector3.Zero;
            return false;
        }

        private void Next()
        {
            _step++;
            _time = 0;
        }

        private void Finish(string result, int code)
        {
            GD.Print($"[DoorTest] {result}");
            GetTree().Quit(code);
        }

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[DoorTest] screenshot {path}");
        }
    }
}
