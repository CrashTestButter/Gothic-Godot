using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using ZenKit.Daedalus;
using Mesh = Godot.Mesh;

namespace GothicGodot.Spike
{
    /// <summary>
    /// The NPCs' side of a fight (spike version of the engine's fight AI): seeing (Npc_CanSeeNpc: senses range, free
    /// line of sight, 50° to each side like Gothic-Unity), targets, equipped and drawn weapons (the weapon's mesh in
    /// the right hand's slot), and AI_Attack: run up to the target, swing, the hit lands half way through the swing
    /// if the target is still in reach and in front. Fight states loop fast (the engine loops whenever the queue is
    /// empty; the spike's routines only every perception time).
    /// </summary>
    public partial class GothicAi
    {
        private const float _runSpeed = 3.5f;
        private const float _fistReach = 1.2f;
        private const float _sightHalfAngle = 50f;

        public class AttackAction : GothicNpc.Action
        {
            public bool IsSwinging;
            public bool IsHitDone;
            public double SwingLength;
        }

        private readonly Dictionary<int, bool> _isFightState = new();
        private int _meleeFlag = -1;

        /// <summary>Meshes for weapons in NPC hands, by item instance (set by the world builder).</summary>
        public Func<string, Mesh> ItemMesh { get; set; }

        /// <summary>The hero was hit: damage, hit points left.</summary>
        public event Action<GothicNpc, int, int> HeroHit;

        private bool IsFightState(int state)
        {
            if (state <= 0)
                return false;
            if (!_isFightState.TryGetValue(state, out var isFight))
            {
                var name = _vm.GetSymbolByIndex(state)?.Name ?? "";
                _isFightState[state] = isFight = name.Contains("ATTACK") || name.Contains("FLEE") ||
                                                 name.Contains("ASSESSENEMY") || name.Contains("REACTTODAMAGE");
            }

            return isFight;
        }

        private double LoopInterval(GothicNpc npc) => IsFightState(npc.StateMain) ? 0.1 : npc.PerceptionTime;

        private bool CanSee(GothicNpc self, GothicNpc other, bool isFreeLineOfSight)
        {
            if (self?.IsActive != true || other?.IsActive != true || self == other)
                return false;
            var eyes = self.Position + Vector3.Up * 1.6f;
            var target = other.Position + Vector3.Up * 1.4f;
            if (eyes.DistanceTo(target) > self.Instance.SensesRange / 100f)
                return false;
            var toTarget = other.Position - self.Position;
            toTarget.Y = 0;
            var forward = self.Forward;
            forward.Y = 0;
            if (!isFreeLineOfSight && toTarget.LengthSquared() > 0.01f &&
                Mathf.RadToDeg(forward.AngleTo(toTarget)) > _sightHalfAngle)
                return false;
            var ray = PhysicsRayQueryParameters3D.Create(eyes, target, Interaction.PhysicsLayers.World);
            return GetWorld3D().DirectSpaceState.IntersectRay(ray).Count == 0;
        }

        private bool IsMelee(string instance)
        {
            if (_meleeFlag < 0)
                _meleeFlag = GetConstant("ITEM_KAT_NF", 1 << 1);
            var symbol = instance != null ? _vm.GetSymbolByName(instance) : null;
            return symbol is { Type: DaedalusDataType.Instance } &&
                   (_vm.InitInstance<ItemInstance>(symbol)?.MainFlag & _meleeFlag) != 0;
        }

        private void DrawWeapon(GothicNpc npc, bool isDrawn)
        {
            npc.FightMode = isDrawn ? npc.MeleeWeapon != null ? GetConstant("FMODE_MELEE", 2) : GetConstant("FMODE_FIST", 1) : 0;
            if (npc.WeaponNode != null)
                npc.WeaponNode.Visible = isDrawn;
            if (!isDrawn || npc.WeaponNode != null || npc.MeleeWeapon == null || npc.Skeleton == null)
                return;
            var mesh = ItemMesh?.Invoke(npc.MeleeWeapon);
            if (mesh == null)
                return;
            var slot = new BoneAttachment3D { BoneName = "ZS_RIGHTHAND" };
            npc.Skeleton.AddChild(slot);
            var instance = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            slot.AddChild(instance);
            instance.SetInstanceShaderParameter("tint", _light.Sample(npc.Position, new Color(0.6f, 0.6f, 0.6f)));
            npc.WeaponNode = slot;
        }

        private float Reach(GothicNpc npc)
        {
            if (npc.FightMode != GetConstant("FMODE_MELEE", 2) || npc.MeleeWeapon == null)
                return _fistReach;
            var item = _vm.InitInstance<ItemInstance>(npc.MeleeWeapon);
            return Mathf.Max(_fistReach, (item?.Range ?? 100) / 100f + 0.6f);
        }

        private void StartAttack(GothicNpc npc, AttackAction attack)
        {
            if (npc.Target?.IsActive != true || npc.Target.IsDown)
                npc.Current = null;
        }

        /// <summary>Running up to the target, or one swing; true when the action is over.</summary>
        private bool TickAttack(GothicNpc npc, AttackAction attack, double delta)
        {
            var target = npc.Target;
            if (target?.IsActive != true || target.IsDown)
                return true;
            var toTarget = target.Position - npc.Position;
            toTarget.Y = 0;
            var distance = toTarget.Length();
            if (distance > 0.01f)
                npc.FaceTowards(toTarget);

            if (!attack.IsSwinging)
            {
                if (distance > Reach(npc) * 0.85f)
                {
                    // Run straight at him for a moment, on the ground.
                    if (npc.Clip?.Name != RunClip(npc))
                        PlayClip(npc, RunClip(npc), true);
                    var step = toTarget / distance * (float)(delta * _runSpeed);
                    var next = npc.Position + step;
                    var ground = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
                        next + Vector3.Up, next + Vector3.Down * 2f, Interaction.PhysicsLayers.World));
                    if (ground.Count > 0)
                        next.Y = ground["position"].AsVector3().Y;
                    npc.SetPosition(next);
                    return npc.ActionTime > 0.8;
                }

                var swing = SwingClip(npc);
                if (swing == null || !PlayClip(npc, swing, false))
                    return true;
                attack.IsSwinging = true;
                attack.SwingLength = Math.Max(0.4, npc.Clip.Duration);
                npc.ActionTime = 0;
                return false;
            }

            if (!attack.IsHitDone && npc.ActionTime >= attack.SwingLength * 0.45)
            {
                attack.IsHitDone = true;
                var forward = npc.Forward;
                if (distance <= Reach(npc) + 0.4f && (distance < 0.3f || forward.Dot(toTarget / distance) > 0.5f))
                {
                    var damage = Fight.NpcHits(npc, target);
                    if (target == _hero && damage >= 0)
                        HeroHit?.Invoke(npc, damage, target.Instance.GetAttribute(NpcAttribute.HitPoints));
                }
            }

            if (npc.ActionTime < attack.SwingLength)
                return false;
            PlayClip(npc, npc.IdleClip, true);
            return true;
        }

        private string RunClip(GothicNpc npc)
        {
            foreach (var name in new[] { npc.FightMode >= 2 ? "S_1HRUNL" : "S_FISTRUNL", "S_RUNL", "S_FISTRUNL" })
                if (HasClip(npc, name))
                    return name;
            return npc.WalkClip;
        }

        private string SwingClip(GothicNpc npc)
        {
            var names = npc.FightMode >= 2
                ? new[] { "S_1HATTACK", "T_1HATTACKL", "S_FISTATTACK" }
                : new[] { "S_FISTATTACK", "T_FISTATTACKMOVE", "S_1HATTACK" };
            foreach (var name in names)
                if (HasClip(npc, name))
                    return name;
            return null;
        }

        private int[] _guildAttitudes = Array.Empty<int>();
        private int _guildCount;

        /// <summary>
        /// The guild table: G1 fills it through B_InitGuildAttitudes (Wld_SetGuildAttitude), G2 from its GIL_ATTITUDES
        /// array (Gothic-Unity does the same exchange at boot, G2's own call is commented out).
        /// </summary>
        private void InitGuildAttitudes()
        {
            _guildCount = GetConstant("GIL_MAX", 0);
            _guildAttitudes = new int[_guildCount * _guildCount];
            Array.Fill(_guildAttitudes, GetConstant("ATT_NEUTRAL", 2));
            ExchangeGuildAttitudes("GIL_ATTITUDES");
        }

        private void ExchangeGuildAttitudes(string table)
        {
            var symbol = _vm.GetSymbolByName(table);
            if (symbol == null)
                return;
            var size = (int)symbol.Size;
            var side = (int)Math.Sqrt(size);
            for (var a = 0; a < side && a < _guildCount; a++)
            for (var b = 0; b < side && b < _guildCount; b++)
                _guildAttitudes[a * _guildCount + b] = symbol.GetInt((ushort)(a * side + b), null);
        }

        private int GuildAttitude(int a, int b) =>
            a >= 0 && b >= 0 && a < _guildCount && b < _guildCount ? _guildAttitudes[a * _guildCount + b] : GetConstant("ATT_NEUTRAL", 2);

        /// <summary>Like the engine (Gothic-Unity's GetPersonAttitude): towards the hero the NPC's own, else the guilds'.</summary>
        private int Attitude(GothicNpc self, GothicNpc other, bool isTemporaryUsed)
        {
            if (self?.Instance == null || other?.Instance == null)
                return GetConstant("ATT_NEUTRAL", 2);
            if (self == _hero || other == _hero)
            {
                var npc = self == _hero ? other : self;
                if (isTemporaryUsed && npc.TempAttitude >= 0)
                    return npc.TempAttitude;
                if (npc.Attitude >= 0)
                    return npc.Attitude;
            }

            return GuildAttitude(self.Instance.Guild, other.Instance.Guild);
        }

        private void RegisterFightExternals()
        {
            _vm.RegisterExternal("Wld_SetGuildAttitude", (DaedalusVm.ExternalFuncV<int, int, int>)((a, attitude, b) =>
            {
                _externals++;
                if (a >= 0 && b >= 0 && a < _guildCount && b < _guildCount)
                    _guildAttitudes[a * _guildCount + b] = attitude;
            }));
            _vm.RegisterExternal("Wld_GetGuildAttitude", (DaedalusVm.ExternalFunc<int, int, int>)((a, b) =>
            {
                _externals++;
                return GuildAttitude(a, b);
            }));
            if (_vm.GetSymbolByName("Wld_ExchangeGuildAttitudes") != null)
                _vm.RegisterExternal("Wld_ExchangeGuildAttitudes", (DaedalusVm.ExternalFuncV<string>)(name =>
                {
                    _externals++;
                    ExchangeGuildAttitudes(name);
                }));
            _vm.RegisterExternal("Npc_SetAttitude", (DaedalusVm.ExternalFuncV<NpcInstance, int>)((self, attitude) =>
            {
                _externals++;
                if (Npc(self) is { } npc)
                    npc.Attitude = attitude;
            }));
            _vm.RegisterExternal("Npc_SetTempAttitude", (DaedalusVm.ExternalFuncV<NpcInstance, int>)((self, attitude) =>
            {
                _externals++;
                if (Npc(self) is { } npc)
                    npc.TempAttitude = attitude;
            }));
            _vm.RegisterExternal("Npc_GetAttitude", (DaedalusVm.ExternalFunc<int, NpcInstance, NpcInstance>)((self, other) =>
            {
                _externals++;
                return Attitude(Npc(self), Npc(other), true);
            }));
            _vm.RegisterExternal("Npc_GetPermAttitude", (DaedalusVm.ExternalFunc<int, NpcInstance, NpcInstance>)((self, other) =>
            {
                _externals++;
                return Attitude(Npc(self), Npc(other), false);
            }));
            _vm.RegisterExternal("Npc_ClearAIQueue", (DaedalusVm.ExternalFuncV<NpcInstance>)(self =>
            {
                _externals++;
                if (Npc(self) is not { } npc)
                    return;
                npc.Actions.Clear();
                npc.Current = null;
            }));
            _vm.RegisterExternal("Npc_CanSeeNpc", (DaedalusVm.ExternalFunc<int, NpcInstance, NpcInstance>)((self, other) =>
            {
                _externals++;
                return CanSee(Npc(self), Npc(other), false) ? 1 : 0;
            }));
            _vm.RegisterExternal("Npc_CanSeeNpcFreeLOS", (DaedalusVm.ExternalFunc<int, NpcInstance, NpcInstance>)((self, other) =>
            {
                _externals++;
                return CanSee(Npc(self), Npc(other), true) ? 1 : 0;
            }));
            _vm.RegisterExternal("Npc_SetTarget", (DaedalusVm.ExternalFuncV<NpcInstance, NpcInstance>)((self, other) =>
            {
                _externals++;
                if (Npc(self) is { } npc)
                    npc.Target = Npc(other);
            }));
            _vm.RegisterExternal("Npc_GetTarget", (DaedalusVm.ExternalFunc<int, NpcInstance>)(self =>
            {
                _externals++;
                if (Npc(self)?.Target is not { Instance: not null } target)
                    return 0;
                _vm.GlobalOther = target.Instance;
                return 1;
            }));
            foreach (var name in new[] { "Npc_GetNextTarget", "Npc_IsNextTargetAvailable" })
                if (_vm.GetSymbolByName(name) != null)
                    _vm.RegisterExternal(name, (DaedalusVm.ExternalFunc<int, NpcInstance>)(_ => 0));
            _vm.RegisterExternal("EquipItem", (DaedalusVm.ExternalFuncV<NpcInstance, int>)((self, item) =>
            {
                _externals++;
                var name = _vm.GetSymbolByIndex(item)?.Name;
                if (Npc(self) is not { } npc || name == null)
                    return;
                // Like the engine, the equipped item is in the inventory too.
                if (npc.Inventory[name] == 0)
                    npc.Inventory.Add(name);
                if (IsMelee(name))
                    npc.MeleeWeapon = name;
            }));
            _vm.RegisterExternal("Npc_HasEquippedMeleeWeapon", (DaedalusVm.ExternalFunc<int, NpcInstance>)(self =>
            {
                _externals++;
                return Npc(self)?.MeleeWeapon != null ? 1 : 0;
            }));
            _vm.RegisterExternal("Npc_IsInFightMode", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((self, mode) =>
            {
                _externals++;
                return Npc(self)?.FightMode == mode ? 1 : 0;
            }));
            foreach (var (name, isDrawn) in new[] { ("AI_DrawWeapon", true), ("AI_ReadyMeleeWeapon", true), ("AI_RemoveWeapon", false) })
                _vm.RegisterExternal(name, (DaedalusVm.ExternalFuncV<NpcInstance>)(self =>
                {
                    _externals++;
                    if (Npc(self) is { Root: not null } npc)
                        DrawWeapon(npc, isDrawn);
                }));
            _vm.RegisterExternal("AI_Attack", (DaedalusVm.ExternalFuncV<NpcInstance>)(self =>
            {
                _externals++;
                if (Npc(self) is { Root: not null } npc && npc.Target != null && npc.Actions.Count == 0)
                    npc.Actions.Enqueue(new AttackAction());
            }));
            // The body state: standing, or down (the scripts' C_NpcIsDown asks for it).
            _vm.RegisterExternal("Npc_GetBodyState", (DaedalusVm.ExternalFunc<int, NpcInstance>)(self =>
            {
                _externals++;
                var npc = Npc(self);
                if (npc?.IsDead == true)
                    return GetConstant("BS_DEAD", 23);
                return npc?.IsDown == true ? GetConstant("BS_UNCONSCIOUS", 22) : 0;
            }));
        }
    }
}
