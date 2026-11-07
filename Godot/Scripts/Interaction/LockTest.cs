using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Scripted lock test (--lock-test): the hero gets 5 lock picks, goes to the nearest locked chest with a lock
    /// combination, turns once wrong (the lock springs back, the pick may break), then the right combination: the chest
    /// unlocks and opens. Screenshot, then quits.
    /// </summary>
    public partial class LockTest : Node
    {
        private GothicPlayer _player;
        private IReadOnlyList<WorldMob> _mobs;
        private MobOpener _opener;
        private LockPicking _locks;
        private InventoryPanel _bag;
        private string _screenshot;
        private double _time;
        private int _step;
        private WorldMob _mob;
        private int _turn;

        public void Init(GothicPlayer player, IReadOnlyList<WorldMob> mobs, MobOpener opener, LockPicking locks,
            InventoryPanel bag, string screenshot)
        {
            _player = player;
            _mobs = mobs;
            _opener = opener;
            _locks = locks;
            _bag = bag;
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
                    _mob = _mobs.Where(m => m.IsLocked && !string.IsNullOrEmpty(m.PickString) && !m.IsDoor)
                        .OrderBy(m => m.Centre.DistanceSquaredTo(_player.GlobalPosition)).FirstOrDefault();
                    if (_mob == null || _locks == null)
                    {
                        Finish("no locked chest with a combination", 1);
                        return;
                    }

                    // G1's lock pick and G2's, and G2's talent.
                    _bag.Inventory.Add("ITKELOCKPICK", 5);
                    _bag.Inventory.Add("ITKE_LOCKPICK", 5);
                    _locks.LearnForTest();
                    var toMob = _player.GlobalPosition - _mob.Centre;
                    toMob.Y = 0;
                    _player.PlaceAt(_mob.Centre + toMob.Normalized() * 1.2f + Vector3.Up * 0.5f, 0);
                    GD.Print($"[LockTest] {_mob.Name} ({_mob.Model}), combination {_mob.PickString}");
                    Next();
                    break;
                case 1 when _time > 1.2:
                    _player.LookAtPoint(_mob.Centre);
                    _opener.Use(_mob);
                    if (!_locks.IsPicking)
                    {
                        Finish("picking didn't start", 1);
                        return;
                    }

                    // One wrong turn first.
                    _locks.ForcedTurn = _mob.PickString[0] == 'L' ? 'R' : 'L';
                    Next();
                    break;
                case 2 when _time > 0.2:
                    if (!_locks.IsPicking && _turn < _mob.PickString.Length)
                    {
                        Finish($"picking stopped after {_turn} turns", 1);
                        return;
                    }

                    if (_turn < _mob.PickString.Length)
                    {
                        _locks.ForcedTurn = _mob.PickString[_turn++];
                        _time = 0;
                        return;
                    }

                    Next();
                    break;
                case 3 when _time > 2.5:
                    Screenshot("open");
                    GD.Print($"[LockTest] locked {_mob.IsLocked}, open {_mob.IsOpen}, " +
                             $"{_bag.Inventory["ITKELOCKPICK"] + _bag.Inventory["ITKE_LOCKPICK"]} lock picks left");
                    Finish(!_mob.IsLocked && _mob.IsOpen ? "done" : "not opened", !_mob.IsLocked && _mob.IsOpen ? 0 : 1);
                    break;
            }
        }

        private void Next()
        {
            _step++;
            _time = 0;
        }

        private void Finish(string result, int code)
        {
            GD.Print($"[LockTest] {result}");
            GetTree().Quit(code);
        }

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[LockTest] screenshot {path}");
        }
    }
}
