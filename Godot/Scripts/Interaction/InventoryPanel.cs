using System;
using System.Collections.Generic;
using Godot;
using GothicGodot.Player;
using ZenKit.Daedalus;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// The hero's bag. Putting away: let go of an item over the shoulder (XR, like Gothic-Unity's backpack) or press B
    /// while holding it (desktop). Taking out: the list opens with Y / the left menu button (XR) or Tab (desktop);
    /// point at an entry or use the right stick (XR), arrow keys / W / S / 1-9 (desktop), and the trigger / Enter puts
    /// the item into the right hand.
    /// </summary>
    public partial class InventoryPanel : Node3D
    {
        private const float _backpackRadius = 0.3f;
        private const float _lineSpacing = 0.036f;
        private const float _width = 0.5f;
        private const int _maxLines = 10;
        private const float _top = 0.17f;
        private static readonly Color _selectedColor = new(1f, 0.85f, 0.45f);
        private static readonly Color _lineColor = new(0.85f, 0.85f, 0.85f);

        private GothicPlayer _player;
        private Inventory _inventory;
        private Func<string, ItemInstance> _info;
        private Func<string, Transform3D, WorldItem> _create;
        private Node3D _itemParent;
        private Node3D _panel;
        private Label3D _title;
        private readonly List<Label3D> _lines = new();
        private int _selected;
        private int _first;
        private bool _wasToggleDown;
        private bool _wasConfirmDown;
        private bool _wasStowDown;
        private bool _wasStickUsed;
        private readonly bool[] _wasKeyDown = new bool[16];

        public bool IsOpen => _panel?.Visible == true;
        public Inventory Inventory => _inventory;

        /// <summary>While false (e.g. in a dialog) the bag stays closed and ignores input.</summary>
        public Func<bool> IsAllowed;

        public void Init(GothicPlayer player, Inventory inventory, Func<string, ItemInstance> info,
            Func<string, Transform3D, WorldItem> create, Node3D itemParent)
        {
            _player = player;
            _inventory = inventory;
            _info = info;
            _create = create;
            _itemParent = itemParent;

            _panel = new Node3D { Name = "Bag", Visible = false };
            AddChild(_panel);
            _title = PanelLabels.Create(26, Colors.White, _width);
            _title.HorizontalAlignment = HorizontalAlignment.Left;
            _title.Position = new Vector3(-_width * 0.5f, _top + _lineSpacing * 1.3f, 0);
            _panel.AddChild(_title);
            for (var i = 0; i < _maxLines; i++)
            {
                var line = PanelLabels.Create(22, _lineColor, _width);
                line.HorizontalAlignment = HorizontalAlignment.Left;
                line.Position = new Vector3(-_width * 0.5f, _top - i * _lineSpacing, 0);
                _panel.AddChild(line);
                _lines.Add(line);
            }

            foreach (var hand in new[] { player.LeftGrab, player.RightGrab })
                if (hand != null)
                    hand.Released += (item, _) => OnReleased(hand, item);
            _inventory.Changed += Refresh;
        }

        public string GetName(string instance)
        {
            var name = _info?.Invoke(instance)?.Name;
            return string.IsNullOrEmpty(name) ? instance : name;
        }

        /// <summary>The item in the hand goes into the bag.</summary>
        public bool Stow(GrabHand hand)
        {
            var item = hand?.TakeAway();
            if (item == null)
                return false;
            _inventory.Add(item.Instance);
            GD.Print($"[Bag] {GetName(item.Instance)} put away ({_inventory[item.Instance]} now)");
            item.QueueFree();
            return true;
        }

        /// <summary>One of the entry's items into the (empty) hand.</summary>
        public bool TakeOut(int index, GrabHand hand)
        {
            if (hand == null || hand.Held != null || index < 0 || index >= _inventory.Count)
                return false;
            var instance = _inventory.Items[index];
            var item = _create?.Invoke(instance, hand.GlobalTransform);
            if (item == null)
            {
                GD.PushWarning($"[Bag] {instance} has no mesh, stays in the bag");
                return false;
            }

            _inventory.Remove(instance);
            _itemParent.AddChild(item);
            hand.Hold(item);
            GD.Print($"[Bag] {GetName(instance)} taken out ({_inventory[instance]} left)");
            return true;
        }

        public void Open()
        {
            _selected = Math.Clamp(_selected, 0, Math.Max(0, _inventory.Count - 1));
            Place();
            _panel.Visible = true;
            Refresh();
        }

        public void Close() => _panel.Visible = false;

        public override void _Process(double delta)
        {
            if (_player == null || !_player.IsPlaced)
                return;
            if (IsAllowed != null && !IsAllowed())
            {
                if (IsOpen)
                    Close();
                return;
            }

            if (Edge(IsToggleDown(), ref _wasToggleDown))
            {
                if (IsOpen)
                    Close();
                else
                    Open();
            }

            if (!_player.IsXr && Edge(Input.IsKeyPressed(Key.B), ref _wasStowDown) && _player.RightGrab?.Held != null)
                Stow(_player.RightGrab);

            if (!IsOpen)
                return;
            FaceHead();
            HandleSelection();
            if (Edge(IsConfirmDown(), ref _wasConfirmDown) && _inventory.Count > 0)
                TakeOut(_selected, _player.RightGrab);
        }

        private void OnReleased(GrabHand hand, WorldItem item)
        {
            // Let go over the shoulder: into the bag (the item was just thrown, take it back out of the world).
            if (!_player.IsXr || !IsInBackpackZone(hand.GlobalPosition))
                return;
            item.Take();
            _inventory.Add(item.Instance);
            GD.Print($"[Bag] {GetName(item.Instance)} put away over the shoulder");
            item.QueueFree();
        }

        /// <summary>Behind the head and a little below it, over either shoulder.</summary>
        private bool IsInBackpackZone(Vector3 position)
        {
            var head = _player.Camera.GlobalTransform;
            var back = head.Origin + head.Basis * new Vector3(0, -0.15f, 0.15f);
            return position.DistanceTo(back) < _backpackRadius;
        }

        /// <summary>Where the eyes look, a little to the left (the right hand points at it), facing the eyes.</summary>
        private void Place()
        {
            var camera = _player.Camera.GlobalTransform;
            var forward = -camera.Basis.Z.Normalized();
            var left = -camera.Basis.X.Normalized();
            GlobalPosition = camera.Origin + forward * 0.7f + left * 0.2f;
            FaceHead();
        }

        private void FaceHead()
        {
            var head = _player.Camera.GlobalPosition;
            var away = GlobalPosition * 2f - head;
            var up = _player.Camera.GlobalBasis.Y;
            if (away.DistanceSquaredTo(GlobalPosition) > 0.0001f)
                LookAt(away, Mathf.Abs((away - GlobalPosition).Normalized().Dot(up)) > 0.99f ? Vector3.Forward : up);
        }

        private void Refresh()
        {
            if (_panel == null || !_panel.Visible)
                return;
            var count = _inventory.Count;
            _selected = Math.Clamp(_selected, 0, Math.Max(0, count - 1));
            _first = Math.Clamp(_first, Math.Max(0, _selected - _maxLines + 1), _selected);
            _title.Text = count == 0 ? "Bag (empty)" : $"Bag ({count})";
            for (var i = 0; i < _lines.Count; i++)
            {
                var index = _first + i;
                var line = _lines[i];
                line.Visible = index < count;
                if (!line.Visible)
                    continue;
                var instance = _inventory.Items[index];
                var amount = _inventory[instance];
                line.Text = amount > 1 ? $"{GetName(instance)}  x{amount}" : GetName(instance);
                line.Modulate = index == _selected ? _selectedColor : _lineColor;
            }
        }

        private void Select(int index)
        {
            var count = _inventory.Count;
            if (count == 0)
                return;
            _selected = (index % count + count) % count;
            Refresh();
        }

        private void HandleSelection()
        {
            if (_player.IsXr)
            {
                var stick = _player.RightHand?.GetVector2("primary").Y ?? 0f;
                if (Mathf.Abs(stick) < 0.3f)
                    _wasStickUsed = false;
                else if (!_wasStickUsed && Mathf.Abs(stick) > 0.6f)
                {
                    _wasStickUsed = true;
                    Select(_selected + (stick > 0 ? -1 : 1));
                }

                var pointed = GetPointedLine();
                if (pointed >= 0 && _first + pointed < _inventory.Count && _first + pointed != _selected)
                    Select(_first + pointed);
                return;
            }

            if (KeyEdge(Key.Up, 0) || KeyEdge(Key.W, 1))
                Select(_selected - 1);
            if (KeyEdge(Key.Down, 2) || KeyEdge(Key.S, 3))
                Select(_selected + 1);
            for (var i = 0; i < 9; i++)
                if (KeyEdge(Key.Key1 + i, 4 + i) && _first + i < _inventory.Count)
                {
                    Select(_first + i);
                    TakeOut(_selected, _player.RightGrab);
                }
        }

        private int GetPointedLine()
        {
            var hand = _player.RightHand;
            if (hand == null || !hand.GetHasTrackingData())
                return -1;
            var origin = hand.GlobalPosition;
            var direction = -hand.GlobalBasis.Z;
            var normal = GlobalBasis.Z;
            var facing = direction.Dot(normal);
            if (Mathf.Abs(facing) < 0.05f)
                return -1;
            var t = (GlobalPosition - origin).Dot(normal) / facing;
            if (t < 0 || t > 3f)
                return -1;
            var local = ToLocal(origin + direction * t);
            if (Mathf.Abs(local.X) > _width * 0.5f || local.Y > _top + _lineSpacing * 0.5f)
                return -1;
            return Mathf.RoundToInt((_top - local.Y) / _lineSpacing);
        }

        private bool IsToggleDown() => _player.IsXr
            ? _player.LeftHand?.IsButtonPressed("by_button") == true ||
              _player.LeftHand?.IsButtonPressed("menu_button") == true
            : Input.IsKeyPressed(Key.Tab);

        private bool IsConfirmDown() => _player.IsXr
            ? _player.RightHand?.IsButtonPressed("trigger_click") == true
            : Input.IsKeyPressed(Key.Enter);

        private bool KeyEdge(Key key, int slot) => Edge(Input.IsKeyPressed(key), ref _wasKeyDown[slot]);

        private static bool Edge(bool isDown, ref bool wasDown)
        {
            var isPressed = isDown && !wasDown;
            wasDown = isDown;
            return isPressed;
        }
    }
}
