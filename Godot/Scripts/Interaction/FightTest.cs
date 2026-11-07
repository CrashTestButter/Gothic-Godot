using System.Linq;
using Godot;
using GothicGodot.Player;
using GothicGodot.Spike;

namespace GothicGodot.Interaction
{
    /// <summary>
    /// Scripted fight test (--fight-test): a sword out of the bag into the right hand, the nearest human that isn't
    /// talking in front of the hero, then desktop swings until he goes down (unconscious). Logs every hit's damage and
    /// the state the NPC reacted with, screenshot of him lying, then quits.
    /// </summary>
    public partial class FightTest : Node
    {
        private static readonly string[] _weapons = { "ITMW_1H_SWORD_01", "ITMW_1H_BAU_MACE", "ITMW_1H_MACE_L_01" };

        private GothicPlayer _player;
        private GothicAi _ai;
        private MeleeSwing _swing;
        private InventoryPanel _bag;
        private string _screenshot;
        private double _time;
        private int _step;
        private GothicNpc _target;
        private int _swings;
        private string _lastState;
        private int _heroHits;
        private bool _wasHeroDown;

        /// <summary>--fight-test=react: one hit, then watch the NPC's answer for 15 s.</summary>
        public bool IsOneHit;

        public void Init(GothicPlayer player, GothicAi ai, MeleeSwing swing, InventoryPanel bag, string screenshot)
        {
            _player = player;
            _ai = ai;
            _swing = swing;
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
                    var weapon = _weapons.FirstOrDefault(w => _ai.Fight.IsMeleeWeapon(w));
                    // A weak one: G2's starting hero mostly near-misses (a tenth of the damage) against guards.
                    // For the reaction test one who survives a hit (G2's citizens have 5 HP).
                    var minHitPoints = IsOneHit ? 20 : 0;
                    var maxHitPoints = IsOneHit ? 200 : 100;
                    _target = _ai.Npcs.Where(n => n.Root != null && n.Factory == _ai.HumanFactory && !n.IsDown &&
                                                  n.Instance.GetAttribute(ZenKit.Daedalus.NpcAttribute.HitPoints) is var hp &&
                                                  hp >= minHitPoints && hp <= maxHitPoints)
                        .OrderBy(n => n.Position.DistanceTo(_player.GlobalPosition)).FirstOrDefault();
                    if (weapon == null || _target == null)
                    {
                        Finish($"no weapon ({weapon}) or no target", 1);
                        return;
                    }

                    _bag.Inventory.Add(weapon);
                    _ai.HeroHit += (npc, _, _) =>
                    {
                        if (npc == _target)
                            _heroHits++;
                    };
                    _player.RightGrab.ForcedGrip = true;
                    _bag.TakeOut(_bag.Inventory.Items.ToList().IndexOf(weapon), _player.RightGrab);
                    GD.Print($"[FightTest] {_bag.GetName(weapon)} against {_target.Name} ({_target.SymbolName}), " +
                             $"{_target.Instance.GetAttribute(ZenKit.Daedalus.NpcAttribute.HitPoints)} hit points");
                    Next();
                    break;
                case 1:
                    // Stand in front of him (he may walk) and swing every 0.6 s.
                    if (_time < 0.6)
                        break;
                    _time = 0;
                    var forward = _target.Forward;
                    forward.Y = 0;
                    _player.PlaceAt(_target.Position + forward.Normalized() * 1.1f, 0);
                    _player.LookAtPoint(_target.Position + Vector3.Up * 1.1f);
                    var hit = _swing.DesktopSwing();
                    _swings++;
                    var state = _ai.Vm.GetSymbolByIndex(_target.StateMain)?.Name ?? "-";
                    GD.Print($"[FightTest] swing {_swings}: {(hit == _target ? "hit" : hit != null ? $"hit {hit.Name}" : "missed")}, " +
                             $"{_target.Instance.GetAttribute(ZenKit.Daedalus.NpcAttribute.HitPoints)} hit points, state {state}");
                    if (_target.IsDown || _swings >= 60 || IsOneHit && _swings >= 1)
                        Next();
                    break;
                case 2 when IsOneHit && _time < 15:
                    _wasHeroDown |= _ai.Hero?.IsDown == true;
                    var name = _ai.Vm.GetSymbolByIndex(_target.StateMain)?.Name ?? "-";
                    if (name != _lastState)
                    {
                        _lastState = name;
                        GD.Print($"[FightTest] {_time:F1} s: {_target.Name} in {name}, target {_target.Target?.Name ?? "-"}, " +
                                 $"fight mode {_target.FightMode}, {_target.Position.DistanceTo(_player.GlobalPosition):F1} m away");
                    }

                    break;
                case 2 when IsOneHit:
                    Screenshot("hit-back");
                    var isAnswered = _heroHits > 0 && _wasHeroDown;
                    Finish(isAnswered
                        ? $"done: {_target.Name} hit back {_heroHits} times, the hero went down"
                        : $"{_target.Name} hit the hero {_heroHits} times, hero down {_wasHeroDown}", isAnswered ? 0 : 1);
                    break;
                case 2 when _time > 2:
                    Screenshot("down");
                    var stateNow = _ai.Vm.GetSymbolByIndex(_target.StateMain)?.Name ?? "-";
                    Finish(_target.IsDown ? $"done: {_target.Name} down after {_swings} swings ({stateNow})"
                        : $"still standing after {_swings} swings", _target.IsDown ? 0 : 1);
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
            GD.Print($"[FightTest] {result}");
            GetTree().Quit(code);
        }

        private void Screenshot(string suffix)
        {
            if (string.IsNullOrEmpty(_screenshot))
                return;
            var path = _screenshot.EndsWith(".png") ? _screenshot[..^4] + $"-{suffix}.png" : $"{_screenshot}-{suffix}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[FightTest] screenshot {path}");
        }
    }
}
