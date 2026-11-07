using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Scripted chest test without a headset (--chest-test): the desktop player walks up to the nearest unlocked chest
    /// with contents, opens it, takes the first item laid out inside and puts it into the bag. Screenshots of the open
    /// chest, then quits.
    /// </summary>
    public partial class ChestTest : Node
    {
        private GothicPlayer _player;
        private IReadOnlyList<WorldMob> _chests;
        private string _screenshot;
        private InventoryPanel _bag;
        private double _time;
        private int _step;
        private WorldMob _chest;
        private WorldItem _item;

        public void Init(GothicPlayer player, IReadOnlyList<WorldMob> chests, string screenshot, InventoryPanel bag)
        {
            _player = player;
            _chests = chests;
            _screenshot = screenshot;
            _bag = bag;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_player == null || !_player.IsPlaced)
                return;
            _time += delta;
            var grab = _player.RightGrab;
            switch (_step)
            {
                case 0 when _time > 2:
                    // Chests and doors further away are made when the camera comes near; the test may pick one.
                    GothicGodot.Spike.NearSpawner.MakeNear(_player.GlobalPosition, 150f);
                    var spot = Vector3.Zero;
                    _chest = _chests.Where(c => !c.IsLocked && c.HasContents)
                        .OrderBy(c => c.Centre.DistanceSquaredTo(_player.GlobalPosition))
                        .FirstOrDefault(c => TryFindSpot(c, out spot));
                    if (_chest == null)
                    {
                        Finish("no reachable unlocked chest", 1);
                        return;
                    }

                    _player.PlaceAt(spot, 0);
                    GD.Print($"[ChestTest] {_chest.Name} ({_chest.Model}) at {_chest.Centre}, standing at {spot}");
                    Next();
                    break;
                case 1 when _time > 1.2:
                    _player.LookAtPoint(_chest.Centre);
                    Next();
                    break;
                case 2 when _time > 0.2:
                    var opener = GetParent().GetNodeOrNull<MobOpener>("MobOpener");
                    if (opener?.FindInView() != _chest)
                    {
                        Finish($"the chest in view is {opener?.FindInView()?.Name ?? "none"}", 1);
                        return;
                    }

                    _chest.Toggle();
                    Next();
                    break;
                case 3 when _time > 2.5:
                    Screenshot("open");
                    if (!_chest.IsOpen || _chest.Items.Count == 0)
                    {
                        Finish($"open {_chest.IsOpen}, {_chest.Items.Count} items inside", 1);
                        return;
                    }

                    _item = _chest.Items[0];
                    _player.LookAtPoint(_item.GlobalPosition);
                    var shape = _item.GetChildren().OfType<CollisionShape3D>().FirstOrDefault();
                    var atItem = _player.GetWorld3D().DirectSpaceState.IntersectPoint(new PhysicsPointQueryParameters3D
                    {
                        Position = _item.GlobalTransform * (shape?.Position ?? Vector3.Zero),
                        CollisionMask = PhysicsLayers.Items
                    });
                    GD.Print($"[ChestTest] first item {_item.Instance} at {_item.GlobalPosition}, " +
                             $"{_item.GlobalPosition.DistanceTo(_player.Camera.GlobalPosition):F2} m from the eyes, shape " +
                             $"{shape?.Shape?.GetType().Name} at {shape?.Position}, layer {_item.CollisionLayer}, " +
                             $"in tree {_item.IsInsideTree()}, point query {atItem.Count} hits");
                    Next();
                    break;
                case 4 when _time > 0.2:
                    grab.ForcedGrip = true;
                    Next();
                    break;
                case 5 when _time > 0.5:
                    if (grab.Held == null)
                    {
                        Finish("nothing taken out of the chest", 1);
                        return;
                    }

                    var instance = grab.Held.Instance;
                    var amount = grab.Held.Amount;
                    var before = _bag?.Inventory[instance] ?? 0;
                    _bag?.Stow(grab);
                    grab.ForcedGrip = false;
                    GD.Print($"[ChestTest] took {instance} x{amount} out of the chest into the bag: {before} -> " +
                             $"{_bag?.Inventory[instance]}");
                    Next();
                    break;
                case 6 when _time > 0.3:
                    Finish("done", 0);
                    break;
            }
        }

        /// <summary>A floor about a metre from the chest from where the eyes see its middle.</summary>
        private bool TryFindSpot(WorldMob chest, out Vector3 spot)
        {
            var space = _player.GetWorld3D().DirectSpaceState;
            var target = chest.Centre;
            GothicGodot.Spike.CollisionStreamer.LoadNear(target, 5f);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.Tau / 8;
                var around = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 1.1f;
                var floor = space.IntersectRay(PhysicsRayQueryParameters3D.Create(around + Vector3.Up * 0.8f,
                    around + Vector3.Down * 2f, PhysicsLayers.World));
                if (floor.Count == 0)
                    continue;
                spot = floor["position"].AsVector3();
                if (Mathf.Abs(spot.Y - (target.Y - 0.4f)) > 0.6f)
                    continue;
                var eyes = spot + Vector3.Up * 1.6f;
                var view = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eyes, target, PhysicsLayers.World));
                if (view.Count == 0 || view["position"].AsVector3().DistanceTo(target) < 0.6f)
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
            GD.Print($"[ChestTest] {result}");
            GetTree().Quit(code);
        }

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[ChestTest] screenshot {path}");
        }
    }
}
