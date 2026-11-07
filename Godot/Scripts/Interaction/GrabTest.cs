using System;
using System.Linq;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Scripted grab test without a headset (--grab-test): the desktop player walks up to the nearest item, takes it
    /// (right hand), turns while holding it, lets go and checks that it flew and landed. Screenshots while holding and
    /// after landing, then quits. With the bag (--grab-test=bag) it puts the item away instead of throwing it, opens
    /// the bag, takes the item out into the hand again and checks the amounts. --grab-test=use then also takes a
    /// potion out of the bag with the hero hurt and drinks it.
    /// </summary>
    public partial class GrabTest : Node
    {
        private GothicPlayer _player;
        private Node3D _items;
        private string _screenshot;
        private double _time;
        private int _step;
        private WorldItem _item;
        private Vector3 _start;
        private Vector3 _releasedAt;
        private InventoryPanel _bag;
        private MouthUse _mouth;
        private Func<ZenKit.Daedalus.NpcInstance> _hero;

        public void Init(GothicPlayer player, Node3D items, string screenshot, InventoryPanel bag = null,
            MouthUse mouth = null, Func<ZenKit.Daedalus.NpcInstance> hero = null)
        {
            _player = player;
            _items = items;
            _screenshot = screenshot;
            _bag = bag;
            _mouth = mouth;
            _hero = hero;
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
                    var spot = Vector3.Zero;
                    _item = _items?.GetChildren().OfType<WorldItem>()
                        .OrderBy(i => i.GlobalPosition.DistanceSquaredTo(_player.GlobalPosition))
                        .Take(200)
                        .FirstOrDefault(i => TryFindSpot(i, out spot));
                    if (_item == null)
                    {
                        Finish("no reachable item near the player", 1);
                        return;
                    }

                    _start = _item.GlobalPosition;
                    _player.PlaceAt(spot, 0);
                    GD.Print($"[GrabTest] {_item.Instance} at {_start}, standing at {spot} " +
                             $"({spot.DistanceTo(_start):F1} m away)");
                    Next();
                    break;
                case 1 when _time > 1.2:
                    _player.LookAtPoint(_item.GlobalPosition);
                    grab.ForcedGrip = true;
                    Next();
                    break;
                case 2 when _time > 0.5:
                    if (grab.Held != _item)
                    {
                        Screenshot("aim");
                        Finish($"not taken (held {grab.Held?.Instance ?? "nothing"})", 1);
                        return;
                    }

                    GD.Print($"[GrabTest] holding {_item.Instance}, {_item.GlobalPosition.DistanceTo(_start):F2} m " +
                             "from where it lay");
                    Screenshot("held");
                    Next();
                    if (_bag != null)
                        _step = 10;
                    break;
                case 3:
                    // Swing to the right for 0.6 s, let go while turning: a throw.
                    _player.RotateY(-(float)delta * 2.5f);
                    if (_time < 0.6)
                        break;
                    grab.ForcedGrip = false;
                    _releasedAt = _item.GlobalPosition;
                    Next();
                    break;
                case 4 when _time > 2.5:
                    var landed = _item.GlobalPosition;
                    GD.Print($"[GrabTest] landed at {landed}: {landed.DistanceTo(_releasedAt):F2} m from where it was " +
                             $"let go, {_releasedAt.Y - landed.Y:F2} m lower, sleeping {_item.Sleeping}, " +
                             $"speed {_item.LinearVelocity.Length():F2} m/s");
                    _player.LookAtPoint(landed);
                    Next();
                    break;
                case 5 when _time > 0.3:
                    Screenshot("landed");
                    Next();
                    break;
                case 6 when _time > 0.3:
                    Finish("done", 0);
                    break;

                // With the bag: put away, open, take out again.
                case 10 when _time > 0.3:
                    var instance = _item.Instance;
                    var before = _bag.Inventory[instance];
                    if (!_bag.Stow(grab) || _bag.Inventory[instance] != before + 1 || grab.Held != null)
                    {
                        Finish($"not put away ({_bag.Inventory[instance]} in the bag)", 1);
                        return;
                    }

                    _bag.Open();
                    GD.Print($"[GrabTest] bag: {string.Join(", ", _bag.Inventory.Items.Select(i => $"{_bag.GetName(i)} x{_bag.Inventory[i]}"))}");
                    Next();
                    break;
                case 11 when _time > 0.5:
                    Screenshot("bag");
                    var index = _bag.Inventory.Items.ToList().FindIndex(i => i == _item.Instance.ToUpperInvariant());
                    grab.ForcedGrip = true;
                    if (!_bag.TakeOut(index, grab) || grab.Held == null)
                    {
                        Finish("not taken out", 1);
                        return;
                    }

                    _item = grab.Held;
                    GD.Print($"[GrabTest] {_bag.GetName(_item.Instance)} back in the hand, {_bag.Inventory[_item.Instance]} left in the bag");
                    _bag.Close();
                    Next();
                    break;
                case 12 when _time > 0.5:
                    Screenshot("taken-out");
                    _step = _mouth != null ? 20 : 3;
                    _time = 0;
                    break;

                // Drinking: the item goes back into the bag, the hero is hurt, a potion from the bag is drunk.
                case 20 when _time > 0.3:
                    _bag.Stow(grab);
                    var hero = _hero?.Invoke();
                    // A healing potion: G1's hero starts with only the letter, so the test brings one.
                    if (_bag.Inventory["ITFO_POTION_HEALTH_01"] == 0 && _bag.Inventory["ITPO_HEALTH_01"] == 0)
                        _bag.Inventory.Add(IsConsumable("ITPO_HEALTH_01") ? "ITPO_HEALTH_01" : "ITFO_POTION_HEALTH_01");
                    var items = _bag.Inventory.Items.ToList();
                    var potion = items.FindIndex(i => IsConsumable(i) && i.Contains("HEALTH"));
                    if (potion < 0)
                        potion = items.FindIndex(IsConsumable);
                    if (hero == null || potion < 0)
                    {
                        Finish("no hero or nothing to drink in the bag", 1);
                        return;
                    }

                    hero.SetAttribute(ZenKit.Daedalus.NpcAttribute.HitPoints, 10);
                    _bag.TakeOut(potion, grab);
                    GD.Print($"[GrabTest] hurt to 10 hit points, holding {_bag.GetName(grab.Held.Instance)} ({grab.Held.Instance})");
                    Next();
                    break;
                case 21 when _time > 0.5:
                    var hitPoints = _hero().GetAttribute(ZenKit.Daedalus.NpcAttribute.HitPoints);
                    var isUsed = _mouth.Consume(grab);
                    var now = _hero().GetAttribute(ZenKit.Daedalus.NpcAttribute.HitPoints);
                    if (!isUsed || now <= hitPoints || grab.Held != null)
                    {
                        Finish($"not drunk (hit points {hitPoints} -> {now})", 1);
                        return;
                    }

                    Next();
                    break;
                case 22 when _time > 0.5:
                    Screenshot("drunk");
                    Finish("done", 0);
                    break;
            }
        }

        /// <summary>A floor outdoors 0.8 m from the item, at most 1.3 m below it, from where the eyes see it.</summary>
        private bool TryFindSpot(WorldItem item, out Vector3 spot)
        {
            var space = _player.GetWorld3D().DirectSpaceState;
            var target = item.GlobalPosition;
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.Tau / 8;
                var around = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.8f;
                var floor = space.IntersectRay(PhysicsRayQueryParameters3D.Create(around + Vector3.Up * 0.5f,
                    around + Vector3.Down * 2f, PhysicsLayers.World));
                if (floor.Count == 0)
                    continue;
                spot = floor["position"].AsVector3();
                if (spot.Y < target.Y - 1.3f || spot.Y > target.Y + 0.2f)
                    continue;
                var eyes = spot + Vector3.Up * 1.6f;
                // Outdoors (sky above), so the shots show something.
                if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eyes, eyes + Vector3.Up * 30f,
                        PhysicsLayers.World)).Count > 0)
                    continue;
                var view = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eyes, target, PhysicsLayers.World));
                if (view.Count == 0 || view["position"].AsVector3().DistanceTo(target) < 0.3f)
                    return true;
            }

            spot = Vector3.Zero;
            return false;
        }

        private Func<string, bool> _consumable;

        private bool IsConsumable(string instance) => _consumable?.Invoke(instance) ?? false;

        /// <summary>For the drink step: what the hero can drink or eat.</summary>
        public void SetConsumable(Func<string, bool> consumable) => _consumable = consumable;

        private void Next()
        {
            _step++;
            _time = 0;
        }

        private void Finish(string result, int code)
        {
            GD.Print($"[GrabTest] {result}");
            GetTree().Quit(code);
        }

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[GrabTest] screenshot {path}");
        }
    }
}
