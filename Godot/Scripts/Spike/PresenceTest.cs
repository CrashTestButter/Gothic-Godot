using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Player;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Scripted presence test (--presence-test): the hero goes to an NPC more than 300 m away - it gets its AI and
    /// body there, the NPCs at the start stop and lose theirs - then back, where they come back. Screenshots, quits.
    /// </summary>
    public partial class PresenceTest : Node
    {
        private GothicPlayer _player;
        private GothicAi _ai;
        private string _screenshot;
        private double _time;
        private int _step;
        private Vector3 _start;
        private GothicNpc _far;
        private List<GothicNpc> _startActive;
        private GothicNpc _startNpc;

        public void Init(GothicPlayer player, GothicAi ai, string screenshot)
        {
            _player = player;
            _ai = ai;
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
                    _start = _player.GlobalPosition;
                    _startActive = _ai.Inserted.Where(n => n.IsActive).ToList();
                    _startNpc = _startActive.Where(n => n.HasBody && !n.IsDead)
                        .OrderBy(n => n.Position.DistanceTo(_start)).FirstOrDefault();
                    _far = _ai.Inserted.Where(n => !n.IsLeftOut && !n.IsDead && n.Routine.Count > 0 &&
                                                   n.Position.DistanceTo(_start) > 300f)
                        .OrderBy(n => n.Position.DistanceTo(_start)).FirstOrDefault();
                    if (_far == null || _startNpc == null)
                    {
                        Finish("no NPC more than 300 m away (or none at the start)", 1);
                        return;
                    }

                    GD.Print($"[PresenceTest] at the start {_startActive.Count} active, " +
                             $"{_startActive.Count(n => n.HasBody)} with a body; going to {_far.SymbolName} " +
                             $"{_far.Position.DistanceTo(_start):F0} m away (active {_far.IsActive})");
                    _player.PlaceAt(_far.Position + new Vector3(2f, 0.5f, 0f), 0);
                    Next();
                    break;
                case 1 when _time > 4:
                    _player.LookAtPoint(_far.Position + Vector3.Up * 1.5f);
                    var stillActive = _startActive.Count(n => n.IsActive);
                    GD.Print($"[PresenceTest] far away: {_far.SymbolName} active {_far.IsActive}, body {_far.HasBody}; " +
                             $"{stillActive} of the start's {_startActive.Count} still active, " +
                             $"{_startActive.Count(n => n.HasBody)} with a body");
                    Screenshot("far");
                    if (!_far.IsActive || !_far.HasBody || stillActive > _startActive.Count / 5)
                    {
                        Finish("the far NPC didn't come in or the start's didn't stop", 1);
                        return;
                    }

                    _player.PlaceAt(_start, 0);
                    Next();
                    break;
                case 2 when _time > 4:
                    _player.LookAtPoint(_startNpc.Position + Vector3.Up * 1.5f);
                    var back = _startActive.Count(n => n.IsActive);
                    GD.Print($"[PresenceTest] back at the start: {back} of {_startActive.Count} active again, " +
                             $"{_startActive.Count(n => n.HasBody)} with a body, {_startNpc.SymbolName} body " +
                             $"{_startNpc.HasBody}; {_far.SymbolName} active {_far.IsActive} (in a dialog {_far.IsInDialog})");
                    Screenshot("back");
                    // G1's Diego starts his game start dialog when the hero arrives: an NPC in a dialog stays.
                    var isBack = _startNpc.HasBody && back >= _startActive.Count * 9 / 10 &&
                                 (!_far.IsActive || _far.IsInDialog);
                    Finish(isBack ? "done" : "the start's NPCs didn't come back", isBack ? 0 : 1);
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
            GD.Print($"[PresenceTest] {result}");
            GetTree().Quit(code);
        }

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[PresenceTest] screenshot {path}");
        }
    }
}
