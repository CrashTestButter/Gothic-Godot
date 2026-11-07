using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Player;
using GothicGodot.Spike;
using ZenKit.Daedalus;

namespace GothicGodot.Dialog
{
    /// <summary>
    /// The dialog in the world: the NPC in front of the hero gets a name tag and is talked to with the trigger (XR) or
    /// F (desktop); the hero is pinned while talking. Subtitles and choices are shown on a panel in front of the head,
    /// voices play at the NPC (hero lines in the head). Choose with the right stick or by pointing (XR), arrow keys,
    /// W/S or 1-9 (desktop); trigger / Enter / Space choose or skip a line.
    /// - Scripted test (--dialog-test=NPC_INSTANCE or "nearest"): talks to the NPC, picks options, takes screenshots
    ///   of a subtitle and of the choices, logs every line and quits.
    /// </summary>
    public partial class DialogPanel : Node3D
    {
        private const float _talkDistance = 3f;
        private const float _talkCosAngle = 0.8f;
        private const float _optionSpacing = 0.055f;
        private const float _panelWidth = 0.8f;
        private const int _maxOptions = 10;
        private static readonly Color _selectedColor = new(1f, 0.85f, 0.45f);
        private static readonly Color _optionColor = new(0.78f, 0.78f, 0.78f);

        private GothicAi _ai;
        private GothicDialog _dialog;
        private GothicPlayer _player;
        private Node3D _panel;
        private Label3D _subtitle;
        private readonly List<Label3D> _options = new();
        private Label3D _focusName;
        private AudioStreamPlayer3D _npcVoice;
        private AudioStreamPlayer _heroVoice;
        private GothicNpc _target;
        private double _focusTime;
        private int _selected;
        private bool _wasPlacedForDialog;
        private bool _wasConfirmDown;
        private bool _wasTalkDown;
        private bool _wasStickUsed;
        private readonly bool[] _wasKeyDown = new bool[16];

        // Scripted test
        private string _testNpc;
        private string _screenshot;
        private double _testTime;
        private int _testChoices;
        private bool _isTestStarted;
        private bool _isSubtitleShot;
        private bool _isOptionsShot;
        private bool _wasTestShowingOptions;
        private readonly HashSet<string> _testChosen = new();
        private int _testLines;
        private int _testVoices;

        /// <summary>An NPC to talk to is in front of the hero (F / the trigger talks to it).</summary>
        public bool HasTarget => _target != null && _dialog?.IsActive == false;

        public void Init(GothicAi ai, GothicPlayer player, string testNpc, string screenshot)
        {
            _ai = ai;
            _dialog = ai.Dialog;
            _player = player;
            _testNpc = testNpc;
            _screenshot = screenshot;
            if (!string.IsNullOrEmpty(testNpc))
                _dialog.MaxLineSeconds = 1.2f;

            _panel = new Node3D { Name = "Panel", Visible = false };
            AddChild(_panel);
            _subtitle = CreateLabel(40, Colors.White);
            _subtitle.VerticalAlignment = VerticalAlignment.Bottom;
            _panel.AddChild(_subtitle);
            for (var i = 0; i < _maxOptions; i++)
            {
                var label = CreateLabel(34, _optionColor);
                label.Position = new Vector3(0, -i * _optionSpacing, 0);
                label.HorizontalAlignment = HorizontalAlignment.Left;
                label.Position += new Vector3(-_panelWidth * 0.5f, 0, 0);
                _panel.AddChild(label);
                _options.Add(label);
            }

            _focusName = CreateLabel(48, Colors.White);
            _focusName.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            _focusName.Visible = false;
            AddChild(_focusName);

            _npcVoice = new AudioStreamPlayer3D { Name = "NpcVoice", UnitSize = 6f, MaxDistance = 40f };
            AddChild(_npcVoice);
            _heroVoice = new AudioStreamPlayer { Name = "HeroVoice" };
            AddChild(_heroVoice);

            _dialog.LineStarted += OnLineStarted;
            _dialog.Ended += OnEnded;
        }

        private static Label3D CreateLabel(int fontSize, Color color) =>
            Interaction.PanelLabels.Create(fontSize, color, _panelWidth);

        public override void _Process(double delta)
        {
            if (_dialog == null || _player == null || !_player.IsPlaced)
                return;

            if (!string.IsNullOrEmpty(_testNpc))
                RunTest(delta);

            _player.IsInputLocked = _dialog.IsActive;
            if (!_dialog.IsActive)
            {
                _panel.Visible = false;
                _wasPlacedForDialog = false;
                UpdateFocus(delta);
                if (_target != null && IsTalkPressed())
                    _dialog.Start(_target);
                return;
            }

            _focusName.Visible = false;
            if (!_wasPlacedForDialog)
            {
                PlacePanel();
                _wasPlacedForDialog = true;
            }

            FacePanel();
            _panel.Visible = true;
            var line = _dialog.Current;
            if (line != null)
            {
                ShowOptions(false);
                _subtitle.Visible = true;
                _subtitle.Text = $"{GetSpeakerName(line)}:\n{line.Text}";
                if (IsConfirmPressed())
                {
                    _dialog.Skip();
                    _npcVoice.Stop();
                    _heroVoice.Stop();
                }

                return;
            }

            _subtitle.Visible = false;
            if (!_dialog.IsShowingOptions)
            {
                ShowOptions(false);
                return;
            }

            ShowOptions(true);
            HandleSelection();
            if (IsConfirmPressed())
                Choose(_selected);
        }

        private void Choose(int index)
        {
            _selected = 0;
            _dialog.Choose(index);
        }

        private void ShowOptions(bool isVisible)
        {
            var options = _dialog.Options;
            if (_selected >= options.Count)
                _selected = 0;
            for (var i = 0; i < _options.Count; i++)
            {
                var label = _options[i];
                label.Visible = isVisible && i < options.Count;
                if (!label.Visible)
                    continue;
                label.Text = options[i].Text;
                label.Modulate = i == _selected ? _selectedColor : _optionColor;
            }
        }

        private string GetSpeakerName(GothicDialog.Line line)
        {
            if (line.IsHero)
                return _ai.HeroInstance?.GetName(NpcNameSlot.Slot0) ?? "Hero";
            return line.Speaker?.Name ?? "";
        }

        private void OnLineStarted(GothicDialog.Line line)
        {
            _npcVoice.Stop();
            _heroVoice.Stop();
            if (line.Voice == null)
                return;
            _testVoices++;
            if (line.IsHero || line.Speaker?.Root == null)
            {
                _heroVoice.Stream = line.Voice;
                _heroVoice.Play();
                return;
            }

            _npcVoice.GlobalPosition = line.Speaker.Position + Vector3.Up * 1.6f;
            _npcVoice.Stream = line.Voice;
            _npcVoice.Play();
        }

        private void OnEnded()
        {
            _npcVoice.Stop();
            _heroVoice.Stop();
            _panel.Visible = false;
            _selected = 0;
        }

        // ---- Placement ----

        /// <summary>Below the eyes, a metre ahead in the view direction, so the NPC stays visible above it.</summary>
        private void PlacePanel()
        {
            var camera = _player.Camera;
            var head = camera.GlobalPosition;
            var forward = -camera.GlobalBasis.Z;
            forward.Y = 0;
            forward = forward.LengthSquared() > 0.001f ? forward.Normalized() : Vector3.Forward;
            _panel.GlobalPosition = head + forward * 0.9f + Vector3.Down * (_player.IsXr ? 0.3f : 0.2f);
        }

        private void FacePanel()
        {
            var head = _player.Camera.GlobalPosition;
            var away = _panel.GlobalPosition * 2f - head;
            away.Y = _panel.GlobalPosition.Y;
            if (away.DistanceSquaredTo(_panel.GlobalPosition) > 0.0001f)
                _panel.LookAt(away, Vector3.Up);
        }

        private void UpdateFocus(double delta)
        {
            _focusTime -= delta;
            if (_focusTime <= 0)
            {
                _focusTime = 0.2;
                _target = FindTalkTarget();
            }

            _focusName.Visible = _target != null;
            if (_target == null)
                return;
            _focusName.Text = _target.Name;
            _focusName.GlobalPosition = _target.Position + Vector3.Up * 2.05f;
        }

        private GothicNpc FindTalkTarget()
        {
            var camera = _player.Camera;
            var head = camera.GlobalPosition;
            var forward = -camera.GlobalBasis.Z;
            forward.Y = 0;
            forward = forward.Normalized();
            GothicNpc best = null;
            var bestDistance = _talkDistance;
            foreach (var npc in _ai.Npcs)
            {
                if (npc.Root == null || !npc.Root.Visible || npc.IsDown || !_dialog.HasInfos(npc))
                    continue;
                var toNpc = npc.Position + Vector3.Up * 1.5f - head;
                toNpc.Y = 0;
                var distance = toNpc.Length();
                if (distance >= bestDistance || distance < 0.01f || toNpc.Normalized().Dot(forward) < _talkCosAngle)
                    continue;
                best = npc;
                bestDistance = distance;
            }

            return best;
        }

        // ---- Input ----

        private bool IsTalkPressed()
        {
            var isDown = _player.IsXr
                ? _player.RightHand.IsButtonPressed("trigger_click")
                : Input.IsKeyPressed(Key.F);
            return Edge(isDown, ref _wasTalkDown);
        }

        private bool IsConfirmPressed()
        {
            var isDown = _player.IsXr
                ? _player.RightHand.IsButtonPressed("trigger_click")
                : Input.IsKeyPressed(Key.Enter) || Input.IsKeyPressed(Key.Space) ||
                  Input.IsMouseButtonPressed(MouseButton.Left);
            return Edge(isDown, ref _wasConfirmDown);
        }

        private void HandleSelection()
        {
            var count = _dialog.Options.Count;
            if (_player.IsXr)
            {
                var stick = _player.RightHand.GetVector2("primary").Y;
                if (Mathf.Abs(stick) < 0.3f)
                    _wasStickUsed = false;
                else if (!_wasStickUsed && Mathf.Abs(stick) > 0.6f)
                {
                    _wasStickUsed = true;
                    _selected = (_selected + (stick > 0 ? -1 : 1) + count) % count;
                }

                var pointed = GetPointedOption();
                if (pointed >= 0 && pointed < count)
                    _selected = pointed;
                return;
            }

            if (KeyEdge(Key.Up, 0) || KeyEdge(Key.W, 1))
                _selected = (_selected - 1 + count) % count;
            if (KeyEdge(Key.Down, 2) || KeyEdge(Key.S, 3))
                _selected = (_selected + 1) % count;
            for (var i = 0; i < Mathf.Min(9, count); i++)
                if (KeyEdge(Key.Key1 + i, 4 + i))
                    Choose(i);
        }

        /// <summary>The option the right hand points at: its ray hits the panel's plane inside an option's row.</summary>
        private int GetPointedOption()
        {
            var hand = _player.RightHand;
            if (hand == null || !hand.GetHasTrackingData())
                return -1;
            var origin = hand.GlobalPosition;
            var direction = -hand.GlobalBasis.Z;
            var normal = _panel.GlobalBasis.Z;
            var facing = direction.Dot(normal);
            if (Mathf.Abs(facing) < 0.05f)
                return -1;
            var t = (_panel.GlobalPosition - origin).Dot(normal) / facing;
            if (t < 0 || t > 5f)
                return -1;
            var local = _panel.ToLocal(origin + direction * t);
            if (Mathf.Abs(local.X) > _panelWidth * 0.5f || local.Y > _optionSpacing * 0.5f)
                return -1;
            return Mathf.RoundToInt(-local.Y / _optionSpacing);
        }

        private bool KeyEdge(Key key, int slot) => Edge(Input.IsKeyPressed(key), ref _wasKeyDown[slot]);

        private static bool Edge(bool isDown, ref bool wasDown)
        {
            var isPressed = isDown && !wasDown;
            wasDown = isDown;
            return isPressed;
        }

        // ---- Scripted test ----

        private void RunTest(double delta)
        {
            _testTime += delta;
            if (!_isTestStarted)
            {
                if (_testTime < 2)
                    return;
                _isTestStarted = true;
                StartTest();
                return;
            }

            if (_dialog.Current != null)
            {
                if (!_isSubtitleShot && _testTime > 0.6)
                {
                    _isSubtitleShot = true;
                    Screenshot("subtitle");
                }

                return;
            }

            if (_dialog.IsShowingOptions)
            {
                // Let the panel draw before the shot and the choice.
                if (!_wasTestShowingOptions)
                {
                    _wasTestShowingOptions = true;
                    _testTime = 0;
                }

                if (_testTime < 0.5)
                    return;
                _testTime = 0;
                var options = _dialog.Options;
                GD.Print("[DialogTest] options: " + string.Join(" | ", options.Select(o => o.Text)));
                if (!_isOptionsShot)
                {
                    _isOptionsShot = true;
                    Screenshot("options");
                    return;
                }

                // A few new options first (not the last one: END or "back"), then the last one.
                var last = options.Count - 1;
                var index = last;
                if (_testChoices < 4)
                {
                    var fresh = Enumerable.Range(0, options.Count).Where(i => !_testChosen.Contains(options[i].Text)).ToList();
                    index = fresh.FirstOrDefault(i => i < last, fresh.Count > 0 ? fresh[0] : last);
                }

                _testChosen.Add(options[index].Text);
                _testChoices++;
                _wasTestShowingOptions = false;
                Choose(index);
                return;
            }

            if (!_dialog.IsActive && _testChoices > 0 || _testTime > 120)
            {
                GD.Print($"[DialogTest] done: {_testChoices} choices, {_testLines} lines, {_testVoices} with voice");
                GetTree().Quit();
            }
        }

        private void StartTest()
        {
            var npcs = _ai.Npcs.Where(n => n.Root != null && _dialog.HasInfos(n));
            var npc = _testNpc == "nearest"
                ? npcs.OrderBy(n => n.Position.DistanceTo(_player.GlobalPosition))
                    .FirstOrDefault(n => _dialog.CountAvailable(n) >= 3)
                : npcs.FirstOrDefault(n => n.Instance.GetName(NpcNameSlot.Slot0) == _testNpc ||
                                           SymbolName(n) == _testNpc.ToUpperInvariant());
            if (npc == null)
            {
                GD.PushWarning($"[DialogTest] no NPC {_testNpc} with infos");
                GetTree().Quit(1);
                return;
            }

            // The hero 1.2 m in front of the NPC, facing it.
            var forward = -npc.Root.GlobalBasis.Z;
            forward.Y = 0;
            forward = forward.Normalized();
            var position = npc.Position + forward * 1.2f;
            _player.PlaceAt(position, Mathf.Atan2(forward.X, forward.Z));
            GD.Print($"[DialogTest] {npc.Name} ({SymbolName(npc)}) at {npc.Position}, {_dialog.CountAvailable(npc)} infos available, {_ai.DescribeLook(npc)}");
            _dialog.LineStarted += _ => _testLines++;
            Callable.From(() =>
            {
                _testTime = 0;
                if (!_dialog.Start(npc))
                {
                    GD.PushWarning("[DialogTest] dialog didn't start");
                    GetTree().Quit(1);
                }
            }).CallDeferred();
        }

        private string SymbolName(GothicNpc npc) => npc.Root?.Name.ToString().ToUpperInvariant();

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            Callable.From(() =>
            {
                GetViewport().GetTexture().GetImage().SavePng(path);
                GD.Print($"[DialogTest] screenshot {path}");
            }).CallDeferred();
        }
    }
}
