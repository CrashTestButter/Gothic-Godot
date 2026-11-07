using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using ZenKit.Daedalus;

namespace GothicGodot.Spike
{
    /// <summary>
    /// The hero's hits, with Gothic-Unity's FightService rules (from OpenGothic): per damage type of the weapon
    /// strength + damage - protection; G1 a critical hit (the talent's value as chance) multiplies the weapon's damage,
    /// G2's hit chance decides between a hit and a near miss (a tenth). At least NPC_MINIMAL_DAMAGE unless immune,
    /// nothing for NPC_FLAG_IMMORTAL. The victim perceives the damage (PERC_ASSESSDAMAGE, other = the hero). At no hit
    /// points humans go down unconscious (ZS_Unconscious), monsters die (ZS_Dead, other = the killer).
    /// </summary>
    public class GothicFight
    {
        private const double _hitCooldown = 0.5;

        private readonly GothicAi _ai;
        private readonly DaedalusVm _vm;
        private readonly bool _isGothic2;
        private readonly System.Random _random = new();
        private int? _assessDamage;

        public GothicFight(GothicAi ai, DaedalusVm vm, bool isGothic2)
        {
            _ai = ai;
            _vm = vm;
            _isGothic2 = isGothic2;
        }

        /// <summary>Melee weapons (ITEM_KAT_NF): what a swing hits with.</summary>
        public bool IsMeleeWeapon(string instance)
        {
            var item = GetItem(instance);
            var meleeFlag = _ai.GetConstant("ITEM_KAT_NF", 1 << 1);
            return item != null && (item.MainFlag & meleeFlag) != 0;
        }

        private ItemInstance GetItem(string instance)
        {
            var symbol = _vm.GetSymbolByName(instance);
            return symbol is { Type: DaedalusDataType.Instance } ? _vm.InitInstance<ItemInstance>(symbol) : null;
        }

        /// <returns>The damage done, or -1 if the hit didn't count (cooldown, already down, in a dialog).</returns>
        public int HeroHits(GothicNpc target, string weapon, double time)
        {
            var hero = _ai.HeroInstance;
            if (hero == null || target?.Instance == null || target.IsDown || target.IsInDialog)
                return -1;
            // One swing can pass through the hitbox for several frames.
            if (time - target.LastHitTime < _hitCooldown)
                return -1;
            target.LastHitTime = time;

            var item = weapon != null ? GetItem(weapon) : null;
            var damage = Damage(hero, target.Instance, item, out var isImmune);
            if (!isImmune)
                damage = Math.Max(damage, _ai.GetConstant("NPC_MINIMAL_DAMAGE", 0));
            if ((target.Instance.Flags & 2) != 0)
                damage = 0;

            var hitPoints = target.Instance.GetAttribute(NpcAttribute.HitPoints) - damage;
            target.Instance.SetAttribute(NpcAttribute.HitPoints, Math.Max(hitPoints, 0));
            GD.Print($"[Fight] hero hits {target.Name} with {item?.Name ?? "fists"}: {damage} damage, " +
                     $"{Math.Max(hitPoints, 0)} / {target.Instance.GetAttribute(NpcAttribute.HitPointsMax)} left");

            if (hitPoints <= 0)
            {
                if (target.Factory == _ai.HumanFactory && _vm.GetSymbolByName("ZS_UNCONSCIOUS") != null)
                    KnockOut(target);
                else
                    Kill(target);
                return damage;
            }

            _ai.PlayClip(target, _ai.HasClip(target, "T_GOTHIT") ? "T_GOTHIT" : target.IdleClip, false);
            _assessDamage ??= _ai.GetConstant("PERC_ASSESSDAMAGE", 0);
            if (target.Perceptions.TryGetValue(_assessDamage.Value, out var reaction))
                _ai.RunState(target, reaction, hero);
            return damage;
        }

        private int Damage(NpcInstance attacker, NpcInstance target, ItemInstance weapon, out bool isImmune)
        {
            var mask = weapon?.DamageType ?? attacker.DamageType;
            if (mask == 0)
                mask = 1 << (int)DamageType.Blunt;
            var isTwoHanded = weapon != null && (weapon.Flags & (_ai.GetConstant("ITEM_2HD_SWD", 1 << 22) |
                                                                 _ai.GetConstant("ITEM_2HD_AXE", 1 << 23))) != 0;
            var roll = _random.Next(100);
            bool isCritical;
            if (_isGothic2)
            {
                isCritical = attacker.GetHitChance(isTwoHanded ? NpcTalent.TwoHanded : NpcTalent.OneHanded) > roll;
            }
            else
            {
                var talent = _ai.GetConstant(isTwoHanded ? "NPC_TALENT_2H" : "NPC_TALENT_1H", isTwoHanded ? 2 : 1);
                isCritical = weapon != null && (_ai.Hero?.TalentValues.GetValueOrDefault(talent) ?? 0) > roll;
            }

            var multiplier = _ai.GetConstant("DAM_CRITICAL_MULTIPLIER", 2);
            var strength = attacker.GetAttribute(NpcAttribute.Strength);
            var damage = 0;
            isImmune = true;
            for (var i = 0; i < 8; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;
                var protection = target.GetProtection((DamageType)i);
                if (protection < 0)
                    continue;
                var typeDamage = attacker.GetDamage((DamageType)i) +
                                 (weapon != null ? weapon.GetDamage((DamageType)i) + weapon.DamageTotal : 0);
                int value;
                if (_isGothic2)
                {
                    value = Math.Max(strength + typeDamage - protection, 0);
                    if (!isCritical)
                        value = (value - 1) / 10;
                }
                else
                {
                    value = Math.Max(strength + (isCritical ? multiplier * typeDamage : typeDamage) - protection, 0);
                }

                damage += value;
                isImmune = false;
            }

            return damage;
        }

        private void KnockOut(GothicNpc target)
        {
            target.Instance.SetAttribute(NpcAttribute.HitPoints, 1);
            target.IsDown = true;
            GD.Print($"[Fight] {target.Name} goes down unconscious");
            _ai.StartStateNow(target, _vm.GetSymbolByName("ZS_UNCONSCIOUS").Index, _ai.HeroInstance);
            _ai.PlayClip(target, FirstClip(target, "T_STAND_2_WOUNDED", "S_WOUNDED", "T_DEAD"), false);
        }

        private void Kill(GothicNpc target)
        {
            target.Instance.SetAttribute(NpcAttribute.HitPoints, 0);
            target.IsDown = true;
            target.IsDead = true;
            GD.Print($"[Fight] {target.Name} dies");
            var dead = _vm.GetSymbolByName("ZS_DEAD");
            if (dead != null)
                _ai.StartStateNow(target, dead.Index, _ai.HeroInstance);
            _ai.PlayClip(target, FirstClip(target, "T_DEAD", "T_DEADB", "S_DEAD"), false);
        }

        private string FirstClip(GothicNpc npc, params string[] names)
        {
            foreach (var name in names)
                if (_ai.HasClip(npc, name))
                    return name;
            return npc.IdleClip;
        }
    }
}
