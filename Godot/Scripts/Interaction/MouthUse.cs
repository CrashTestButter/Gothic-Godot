using System;
using Godot;
using GothicGodot.Player;
using ZenKit.Daedalus;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Eating and drinking: food or a potion held at the mouth for a moment (XR) or U while holding it (desktop) is
    /// used up - its script runs (a potion heals, food feeds). Shows the hero's hit points: on the left wrist (XR) or
    /// top left (desktop).
    /// </summary>
    public partial class MouthUse : Node
    {
        private const float _mouthRadius = 0.12f;
        private const double _holdSeconds = 0.3;

        private GothicPlayer _player;
        private Func<string, bool> _isConsumable;
        private Func<string, bool> _use;
        private Func<string, string> _name;
        private Func<NpcInstance> _hero;
        private Label3D _hitPoints;
        private readonly double[] _atMouth = new double[2];
        private bool _wasUseDown;
        private string _shown;

        public void Init(GothicPlayer player, Func<string, bool> isConsumable, Func<string, bool> use,
            Func<string, string> name, Func<NpcInstance> hero)
        {
            _player = player;
            _isConsumable = isConsumable;
            _use = use;
            _name = name;
            _hero = hero;

            _hitPoints = PanelLabels.Create(24, new Color(1f, 0.55f, 0.5f), 0.3f);
            _hitPoints.HorizontalAlignment = HorizontalAlignment.Left;
            if (player.IsXr && player.LeftHand != null)
            {
                // On the inside of the left wrist, readable when the arm is turned up.
                _hitPoints.Position = new Vector3(0.04f, 0.03f, 0.1f);
                _hitPoints.Rotation = new Vector3(-Mathf.Pi * 0.5f, Mathf.Pi * 0.5f, 0);
                player.LeftHand.AddChild(_hitPoints);
            }
            else
            {
                _hitPoints.Position = new Vector3(-0.42f, 0.25f, -0.6f);
                player.Camera.AddChild(_hitPoints);
            }
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_player == null || !_player.IsPlaced)
                return;
            UpdateHitPoints();

            if (_player.IsXr)
            {
                CheckMouth(_player.LeftGrab, 0, delta);
                CheckMouth(_player.RightGrab, 1, delta);
                return;
            }

            var isUseDown = Input.IsKeyPressed(Key.U);
            if (isUseDown && !_wasUseDown)
                Consume(_player.RightGrab);
            _wasUseDown = isUseDown;
        }

        private void CheckMouth(GrabHand hand, int slot, double delta)
        {
            if (hand?.Held == null || !_isConsumable(hand.Held.Instance))
            {
                _atMouth[slot] = 0;
                return;
            }

            var head = _player.Camera.GlobalTransform;
            var mouth = head.Origin + head.Basis * new Vector3(0, -0.07f, -0.06f);
            _atMouth[slot] = hand.Held.GlobalPosition.DistanceTo(mouth) < _mouthRadius ? _atMouth[slot] + delta : 0;
            if (_atMouth[slot] >= _holdSeconds)
            {
                _atMouth[slot] = 0;
                Consume(hand);
            }
        }

        /// <summary>The held food or potion is used up; false if it isn't one.</summary>
        public bool Consume(GrabHand hand)
        {
            var item = hand?.Held;
            if (item == null || !_isConsumable(item.Instance))
                return false;
            var hero = _hero();
            var before = hero?.GetAttribute(NpcAttribute.HitPoints) ?? 0;
            hand.TakeAway();
            item.QueueFree();
            var isUsed = _use(item.Instance);
            var after = hero?.GetAttribute(NpcAttribute.HitPoints) ?? 0;
            GD.Print($"[Use] {_name(item.Instance)} {(isUsed ? "used" : "has no use function")}: " +
                     $"hit points {before} -> {after}");
            return isUsed;
        }

        private void UpdateHitPoints()
        {
            var hero = _hero();
            if (hero == null)
                return;
            var text = $"HP {hero.GetAttribute(NpcAttribute.HitPoints)} / {hero.GetAttribute(NpcAttribute.HitPointsMax)}";
            if (text == _shown)
                return;
            _shown = text;
            _hitPoints.Text = text;
        }
    }
}
