using System;
using System.Collections.Generic;
using Godot;
using GothicGodot.Player;
using GothicGodot.Spike;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Locked chests and doors, with Gothic's rules (like Gothic-Unity's LockPickService, from OpenGothic): the key
    /// item opens at once; else the lock combination ("LRRL") is turned with a lock pick - G1 everybody, G2 only with
    /// the lock picking talent. A wrong turn starts over and may break the pick: G1 by the talent's value (the chance to
    /// break, 90 untrained), G2 by the hero's dexterity. Every turn calls the scripts' G_PickLock(success, brokenOpen).
    /// Turns: arrow keys left / right (desktop), the right stick left / right (XR); Escape or B stops.
    /// </summary>
    public partial class LockPicking : Node
    {
        public enum Result
        {
            Unlocked,
            Picking,
            Refused
        }

        private const float _maxDistance = 2.5f;

        private GothicPlayer _player;
        private GothicAi _ai;
        private Inventory _inventory;
        private bool _isGothic2;
        private readonly Random _random = new();
        private WorldMob _mob;
        private int _progress;
        private Label3D _label;
        private bool _wasLeftDown;
        private bool _wasRightDown;
        private bool _wasStickUsed;

        public bool IsPicking => _mob != null;

        /// <summary>Tests: the next turn instead of the input ('L' or 'R').</summary>
        public char? ForcedTurn;

        public void Init(GothicPlayer player, GothicAi ai, Inventory inventory, bool isGothic2)
        {
            _player = player;
            _ai = ai;
            _inventory = inventory;
            _isGothic2 = isGothic2;
            _label = PanelLabels.Create(30, new Color(0.9f, 0.85f, 0.6f), 0.6f);
            _label.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            _label.Visible = false;
            AddChild(_label);
        }

        private string LockPickItem =>
            _inventory["ITKE_LOCKPICK"] > 0 ? "ITKE_LOCKPICK" : _inventory["ITKELOCKPICK"] > 0 ? "ITKELOCKPICK" : null;

        /// <summary>A locked mob is to be opened: with the key, by picking its lock, or not at all.</summary>
        public Result Try(WorldMob mob)
        {
            if (!mob.IsLocked)
                return Result.Unlocked;
            if (!string.IsNullOrEmpty(mob.Key) && _inventory[mob.Key] > 0)
            {
                GD.Print($"[Lock] {mob.Name} opened with {mob.Key}");
                mob.Unlock();
                return Result.Unlocked;
            }

            if (string.IsNullOrEmpty(mob.PickString))
            {
                GD.Print($"[Lock] {mob.Name} needs the key {mob.Key}");
                return Result.Refused;
            }

            var picklock = _ai?.GetConstant("NPC_TALENT_PICKLOCK", 5) ?? 5;
            var canPick = !_isGothic2 || (_ai?.Hero?.TalentSkills.GetValueOrDefault(picklock) ?? 0) > 0;
            if (!canPick || LockPickItem == null)
            {
                GD.Print($"[Lock] {mob.Name}: {(canPick ? "no lock pick" : "the hero can't pick locks")}");
                _ai?.CallAsHero("PLAYER_MOB_MISSING_LOCKPICK");
                return Result.Refused;
            }

            _mob = mob;
            _progress = 0;
            _player.IsInputLocked = true;
            UpdateLabel();
            GD.Print($"[Lock] picking {mob.Name} ({mob.PickString.Length} turns)");
            return Result.Picking;
        }

        /// <summary>Tests: the hero learns lock picking (G2 needs the talent).</summary>
        public void LearnForTest()
        {
            if (_ai?.Hero != null)
                _ai.Hero.TalentSkills[_ai.GetConstant("NPC_TALENT_PICKLOCK", 5)] = 1;
        }

        public void Stop()
        {
            if (_mob == null)
                return;
            _mob = null;
            _label.Visible = false;
            _player.IsInputLocked = false;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_mob == null)
                return;
            if (_player.GlobalPosition.DistanceTo(_mob.Centre) > _maxDistance || IsStopPressed())
            {
                GD.Print($"[Lock] stopped picking {_mob.Name}");
                Stop();
                return;
            }

            var turn = ForcedTurn ?? ReadTurn();
            ForcedTurn = null;
            if (turn != null)
                Turn(turn.Value);
        }

        private void Turn(char direction)
        {
            var combination = _mob.PickString;
            if (direction == combination[_progress])
            {
                _progress++;
                var isOpen = _progress >= combination.Length;
                _ai?.CallAsHero("G_PickLock", 1, isOpen ? 1 : 0);
                if (isOpen)
                {
                    var mob = _mob;
                    Stop();
                    mob.Unlock();
                    mob.Toggle();
                    return;
                }

                UpdateLabel();
                return;
            }

            // Wrong: the lock springs back, and the pick may break.
            _progress = 0;
            if (RollsBreak())
            {
                var item = LockPickItem;
                _inventory.Remove(item);
                _ai?.CallAsHero("G_PickLock", 0, 1);
                GD.Print($"[Lock] the lock pick broke ({_inventory[item]} left)");
                if (LockPickItem == null)
                {
                    Stop();
                    return;
                }
            }
            else
            {
                _ai?.CallAsHero("G_PickLock", 0, 0);
            }

            UpdateLabel();
        }

        private bool RollsBreak()
        {
            var hero = _ai?.HeroInstance;
            if (hero == null)
                return false;
            if (_isGothic2)
                return hero.GetAttribute(ZenKit.Daedalus.NpcAttribute.Dexterity) <= _random.Next(100);
            var picklock = _ai.GetConstant("NPC_TALENT_PICKLOCK", 5);
            return _random.Next(100) < (_ai.Hero?.TalentValues.GetValueOrDefault(picklock) ?? 0);
        }

        private char? ReadTurn()
        {
            if (_player.IsXr)
            {
                var x = _player.RightHand?.GetVector2("primary").X ?? 0f;
                if (Mathf.Abs(x) < 0.3f)
                {
                    _wasStickUsed = false;
                    return null;
                }

                if (_wasStickUsed || Mathf.Abs(x) < 0.7f)
                    return null;
                _wasStickUsed = true;
                return x < 0 ? 'L' : 'R';
            }

            var left = Input.IsKeyPressed(Key.Left);
            var right = Input.IsKeyPressed(Key.Right);
            char? turn = left && !_wasLeftDown ? 'L' : right && !_wasRightDown ? 'R' : null;
            _wasLeftDown = left;
            _wasRightDown = right;
            return turn;
        }

        private bool IsStopPressed() => _player.IsXr
            ? _player.RightHand?.IsButtonPressed("by_button") == true
            : Input.IsKeyPressed(Key.Escape);

        private void UpdateLabel()
        {
            _label.Visible = true;
            _label.GlobalPosition = _mob.ClosestPoint(_player.Camera.GlobalPosition) + Vector3.Up * 0.25f;
            var picks = _inventory[LockPickItem ?? "ITKELOCKPICK"];
            // Like the click Gothic plays for a right turn: how many held, not how many are left.
            _label.Text = $"Lock {new string('|', _progress)}  ({picks} lock picks)";
        }
    }
}
