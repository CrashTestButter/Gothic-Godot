using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using ZenKit;
using ZenKit.Daedalus;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Game logic load test: the Old Camp's real NPCs from G1's startup script, with their look, daily routine and
    /// AI states running in the Daedalus VM every frame like Gothic-Unity's AiHandler: the state loop runs when the
    /// action queue is empty (at most once per perception time), active perceptions on a timer, actions play
    /// animations, wait or walk the waynet. Externals the states need are implemented cheaply, the rest go to the
    /// default handler. Logs VM time, AI time and external calls once per second.
    /// </summary>
    public partial class GothicAi : Node3D
    {
        private const int _loopContinue = 0;
        private const float _walkSpeed = 1.6f;
        private int _percAssessPlayer = 1;

        private DaedalusVm _vm;
        private ModelFactory _factory;
        private Vfs _vfs;
        private ZenKit.World _world;
        private GroundLightSampler _light;
        private readonly List<GothicNpc> _npcs = new();
        private readonly Dictionary<int, GothicNpc> _npcBySymbol = new();
        private readonly List<GothicNpc> _inserted = new();
        private readonly Dictionary<Area3D, GothicNpc> _npcByHitbox = new();

        /// <summary>The NPC a physics query hit (its hitbox area).</summary>
        public GothicNpc NpcOf(GodotObject collider) =>
            collider is Area3D area && _npcByHitbox.TryGetValue(area, out var npc) ? npc : null;
        private readonly Dictionary<string, Vector3> _waypoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _waynet = new(StringComparer.OrdinalIgnoreCase);
        private readonly Random _random = new(3);
        private GothicNpc _hero;
        private int _minuteOfDay;
        private double _time;

        // Measurements
        private readonly Stopwatch _vmWatch = new();
        private readonly Stopwatch _frameWatch = new();
        private double _vmMs;
        private double _aiMs;
        private double _boneMs;
        private long _externals;
        private long _defaultExternals;
        private long _loops;
        private long _perceptions;
        private int _frames;
        private double _logTime;
        private readonly Dictionary<string, int> _missingAnis = new();
        private int _slowCallsLogged;
        private int _skippedMonsters;
        private readonly Dictionary<string, long> _allocatedByState = new();
        private long _allocatedInCalls;
        private long _allocatedAtLastLog;

        public int Count => _npcs.Count;
        public IReadOnlyList<GothicNpc> Npcs => _npcs;
        public DaedalusVm Vm => _vm;
        public ModelFactory HumanFactory => _factory;

        /// <summary>Tests: every state call of this NPC is logged ([Trace]).</summary>
        public GothicNpc TracedNpc { get; set; }
        public GothicFight Fight { get; private set; }

        /// <summary>Items the scripts put into the world (Wld_InsertItem): instance and place.</summary>
        public List<(string Instance, Vector3 Position)> InsertedItems { get; } = new();

        /// <summary>A script inserted an item (after loading the world builder makes it at once).</summary>
        public event Action<string, Vector3> ItemInserted;
        public double Time => _time;

        /// <summary>The scripts' sounds (Snd_Play, Snd_Play3d) play here once it is set.</summary>
        public Audio.GothicSfx Sfx { get; set; }
        public GothicDialog Dialog { get; private set; }
        public NpcInstance HeroInstance => _hero?.Instance;
        public GothicNpc Hero => _hero;
        public Vector3 HeroPosition => _hero?.Root != null ? _hero.Root.GlobalPosition : Vector3.Zero;

        /// <summary>The player becomes the hero the NPCs perceive (instead of the fixed hero position).</summary>
        public void SetHero(Node3D player)
        {
            if (_hero == null)
                return;
            _hero.Root.QueueFree();
            _hero.Root = player;
        }

        public void Init(Vfs vfs, ModelFactory factory, ZenKit.World world, GroundLightSampler light, int hour,
            Vector3 heroPosition, float radius, string startupFunction, string dataPath)
        {
            _factory = factory;
            _vfs = vfs;
            _world = world;
            _light = light;
            _minuteOfDay = hour * 60;

            foreach (var point in world.WayNet.Points)
                _waypoints[point.Name] = MeshPart.ToGodot(point.Position);
            foreach (var vob in world.RootObjects)
                CollectFreePoints(vob);
            foreach (var edge in world.WayNet.Edges)
            {
                AddEdge(edge.A.Name, edge.B.Name);
                AddEdge(edge.B.Name, edge.A.Name);
            }

            _vm = new DaedalusVm(vfs, "GOTHIC.DAT",
                (byte)(DaedalusExecutionFlags.AllowNullInstanceAccess | DaedalusExecutionFlags.IgnoreConstSpecifier));
            RegisterExternals();
            var perc = _vm.GetSymbolByName("PERC_ASSESSPLAYER");
            if (perc != null)
                _percAssessPlayer = perc.GetInt(0, null);

            _hero = new GothicNpc { Name = "Hero", Root = new Node3D { Name = "Hero" } };
            AddChild(_hero.Root);
            _hero.Root.GlobalPosition = heroPosition;
            var hero = _vm.AllocInstance<NpcInstance>("PC_HERO");
            hero.UserData = _hero;
            _hero.Instance = hero;
            _vm.InitInstance(hero);
            _vm.GlobalHero = hero;
            GD.Print($"[Ai] hero after init: HP {hero.GetAttribute(NpcAttribute.HitPoints)} / " +
                     $"{hero.GetAttribute(NpcAttribute.HitPointsMax)}, level {hero.Level}");

            // Like the engine on a new game: the global init (G1: B_InitGuildAttitudes), then the world's startup.
            InitGuildAttitudes();
            var init = _vm.GetSymbolByName("INIT_GLOBAL");
            if (init is { Type: DaedalusDataType.Function, IsExternal: false })
                Measure(() => _vm.Call(init.Index));
            var startup = _vm.GetSymbolByName(startupFunction);
            if (startup != null)
                Measure(() => _vm.Call(startup.Index));

            // Every inserted NPC exists as a script instance (Hlp_GetNpc finds it); only those near get a body and AI.
            var center = heroPosition;
            foreach (var npc in _inserted)
            {
                if (!_waypoints.TryGetValue(npc.SpawnWaypoint ?? "", out var spawnPosition) ||
                    spawnPosition.DistanceTo(center) > radius)
                    continue;
                Spawn(npc);
            }

            Dialog = new GothicDialog(_vm, vfs, this, dataPath);
            // G2's scripts have ITPO_HEALTH_01 (G1: ItFo_Potion_Health_01): the game decides the damage rules.
            Fight = new GothicFight(this, _vm, _vm.GetSymbolByName("ITPO_HEALTH_01") != null);
            GD.Print($"[Dialog] {Dialog.InfoCount} infos, subtitles {(Dialog.HasSubtitles ? "OU.BIN" : "missing")}");

            if (_leftOut.Count > 0)
                GD.Print("[Ai] left out: " + string.Join(", ", _leftOut.Select(l => $"{l.Key} x{l.Value}")));
            _world = null;
            GD.Print($"[Ai] {startupFunction} inserted {_inserted.Count} NPCs, {_npcs.Count} within {radius} m spawned at " +
                     $"{hour:00}:00 ({_monsters} monsters of {_factories.Count(f => f.Value != null)} models, {_skippedMonsters} left out), routines {_npcs.Sum(n => n.Routine.Count)} entries");
        }

        private readonly Dictionary<string, ModelFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _leftOut = new();

        private void NoteLeftOut(string reason)
        {
            _skippedMonsters++;
            _leftOut[reason] = _leftOut.GetValueOrDefault(reason) + 1;
        }
        private int _monsters;

        /// <summary>The model of a visual (HUMANS.MDS, Wolf.mds): HUMANS is the shared one, others load once.</summary>
        private ModelFactory GetFactory(string visual)
        {
            var model = string.IsNullOrEmpty(visual) ? "HUMANS" : Path.GetFileNameWithoutExtension(visual).ToUpperInvariant();
            if (model == _factory.Model)
                return _factory;
            if (_factories.TryGetValue(model, out var cached))
                return cached;
            ModelFactory factory = null;
            try
            {
                factory = new ModelFactory(_vfs, _factory.Textures, _factory.Opaque, _factory.Alpha, model);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Ai] model {model} can't be loaded: {e.Message}");
            }

            _factories[model] = factory;
            return factory;
        }

        private const float _freePointLookup = 7f;
        private readonly List<GothicNpc.FreePoint> _freePoints = new();
        // The 7 m lookups (Wld_IsFPAvailable, called by many states each loop) search the 3x3 cells around the NPC.
        private const float _freePointCell = 8f;
        private readonly Dictionary<(int, int), List<GothicNpc.FreePoint>> _freePointCells = new();

        private static (int, int) FreePointCell(Vector3 position) =>
            ((int)Mathf.Floor(position.X / _freePointCell), (int)Mathf.Floor(position.Z / _freePointCell));

        private void CollectFreePoints(ZenKit.Vobs.IVirtualObject vob)
        {
            if (vob.Type == ZenKit.Vobs.VirtualObjectType.zCVobSpot && !string.IsNullOrEmpty(vob.Name))
            {
                var point = new GothicNpc.FreePoint
                {
                    Name = vob.Name,
                    Position = MeshPart.ToGodot(vob.Position),
                    // Gothic's forward (+Z) is -Z here, like the models.
                    Direction = -VobBuilder.ToGodot(vob.Rotation).Z.Normalized()
                };
                _freePoints.Add(point);
                var cell = FreePointCell(point.Position);
                if (!_freePointCells.TryGetValue(cell, out var list))
                    _freePointCells[cell] = list = new List<GothicNpc.FreePoint>();
                list.Add(point);
            }
            foreach (var child in vob.Children)
                CollectFreePoints(child);
        }

        /// <summary>The nearest free point whose name contains the part (the NPC's own one counts as free).</summary>
        private GothicNpc.FreePoint FindFreePoint(GothicNpc npc, string part, float maxDistance, bool isOtherThanCurrent)
        {
            GothicNpc.FreePoint best = null;
            var bestDistance = maxDistance;
            if (maxDistance < _freePointCell)
            {
                var (cx, cz) = FreePointCell(npc.Position);
                for (var x = cx - 1; x <= cx + 1; x++)
                for (var z = cz - 1; z <= cz + 1; z++)
                {
                    if (!_freePointCells.TryGetValue((x, z), out var cell))
                        continue;
                    foreach (var point in cell)
                        if (IsFreeFor(point, npc, part, isOtherThanCurrent) && point.Position.DistanceTo(npc.Position) is var d &&
                            d < bestDistance)
                        {
                            best = point;
                            bestDistance = d;
                        }
                }

                return best;
            }

            foreach (var point in _freePoints)
            {
                if (point.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (point.Occupant != null && point.Occupant != npc || isOtherThanCurrent && point == npc.CurrentFreePoint)
                    continue;
                var distance = point.Position.DistanceTo(npc.Position);
                if (distance >= bestDistance)
                    continue;
                best = point;
                bestDistance = distance;
            }

            return best;
        }

        private static bool IsFreeFor(GothicNpc.FreePoint point, GothicNpc npc, string part, bool isOtherThanCurrent) =>
            point.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 &&
            (point.Occupant == null || point.Occupant == npc) && !(isOtherThanCurrent && point == npc.CurrentFreePoint);

        private void AddEdge(string from, string to)
        {
            if (!_waynet.TryGetValue(from, out var list))
                _waynet[from] = list = new List<string>();
            list.Add(to);
        }

        /// <summary>
        /// Wld_InsertNpc: the NPC's script instance (attributes, inventory, look) and its daily routine, at once like the
        /// engine - the startup script may change it right after (G2 kills some through Hlp_GetNpc).
        /// </summary>
        private void Insert(int instanceIndex, string waypoint)
        {
            var symbol = _vm.GetSymbolByIndex(instanceIndex);
            if (symbol == null || symbol.Type != DaedalusDataType.Instance)
                return;

            var npc = new GothicNpc { SpawnWaypoint = waypoint, SymbolName = symbol.Name };
            var instance = _vm.AllocInstance<NpcInstance>(symbol);
            instance.UserData = npc;
            npc.Instance = instance;
            var self = _vm.GlobalSelf;
            // The instance's code says self for the NPC being made.
            _vm.GlobalSelf = instance;
            Measure(() => _vm.InitInstance(instance));
            npc.Name = instance.GetName(NpcNameSlot.Slot0);

            // Daily routine: Rtn_* calls TA_* which call TA_Min(self, ...).
            if (instance.DailyRoutine > 0)
            {
                _vm.GlobalSelf = instance;
                Measure(() => _vm.Call(instance.DailyRoutine));
            }

            _vm.GlobalSelf = self;
            _inserted.Add(npc);
            _npcBySymbol[instanceIndex] = npc;
        }

        /// <summary>Body, animation and AI for an inserted NPC near the hero.</summary>
        private void Spawn(GothicNpc npc)
        {
            var waypoint = npc.SpawnWaypoint;

            // Humans and monsters (wolves, sheep, ...): each model with its own skeleton and animations.
            var factory = GetFactory(npc.Visual);
            if (factory == null)
            {
                NoteLeftOut($"model {npc.Visual}");
                return;
            }

            npc.Factory = factory;
            npc.IdleClip = factory.GetClip("S_RUN") != null ? "S_RUN" : "S_FISTRUN";
            npc.WalkClip = factory.GetClip("S_WALKL") != null ? "S_WALKL" : "S_FISTWALKL";
            var armorMdm = factory == _factory ? GetArmorMdm(npc.ArmorInstance) : null;
            var bodyMdm = armorMdm ?? $"{npc.Body.ToUpperInvariant()}.MDM";
            var body = factory.GetSkinnedMesh(bodyMdm, npc.BodyTexture, npc.SkinColor) ??
                       (factory == _factory
                           ? factory.GetSkinnedMesh("HUM_BODY_NAKED0.MDM", npc.BodyTexture, npc.SkinColor)
                           : factory.GetSkinnedMesh($"{factory.Model}.MDL", npc.BodyTexture, npc.SkinColor));
            if (body == null)
            {
                NoteLeftOut($"body {bodyMdm} of {factory.Model}");
                return;
            }

            if (factory != _factory)
                _monsters++;

            npc.Root = new Node3D { Name = npc.SymbolName };
            AddChild(npc.Root);
            // Like the engine on world start: at the waypoint of the routine entry that runs now.
            var current = npc.Routine.FirstOrDefault(r => IsActive(r, _minuteOfDay));
            npc.Root.GlobalPosition = current.Waypoint != null && _waypoints.TryGetValue(current.Waypoint, out var routinePosition)
                ? routinePosition
                : _waypoints[waypoint];
            npc.Skeleton = factory.CreateSkeleton();
            npc.Root.AddChild(npc.Skeleton);
            var tint = _light.Sample(npc.Root.GlobalPosition, new Color(0.6f, 0.6f, 0.6f));
            var bodyInstance = new MeshInstance3D { Mesh = body, Skin = factory.Skin };
            npc.Skeleton.AddChild(bodyInstance);
            bodyInstance.Skeleton = bodyInstance.GetPathTo(npc.Skeleton);
            bodyInstance.SetInstanceShaderParameter("tint", tint);

            var head = string.IsNullOrEmpty(npc.Head) || factory != _factory
                ? null
                : _factory.GetHeadMesh($"{npc.Head.ToUpperInvariant()}.MMB", npc.HeadTexture, npc.SkinColor);
            if (head != null)
            {
                var attachment = new BoneAttachment3D { BoneName = "BIP01 HEAD" };
                npc.Skeleton.AddChild(attachment);
                var headInstance = new MeshInstance3D { Mesh = head };
                attachment.AddChild(headInstance);
                headInstance.SetInstanceShaderParameter("tint", tint);
            }

            // The hitbox for the hero's weapon: the body's box in its rest pose.
            var bounds = body.GetAabb();
            npc.Hitbox = new Area3D
            {
                Name = "Hitbox", CollisionLayer = Interaction.PhysicsLayers.Npcs, CollisionMask = 0, Monitoring = false
            };
            npc.Hitbox.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = bounds.Size * new Vector3(0.8f, 1f, 0.8f) }, Position = bounds.GetCenter()
            });
            npc.Root.AddChild(npc.Hitbox);
            npc.Hitbox.SetMeta("npc", npc.SymbolName);
            _npcByHitbox[npc.Hitbox] = npc;

            PlayClip(npc, npc.IdleClip, true);
            npc.NextPerceptionTime = _random.NextDouble() * 2.0;
            _npcs.Add(npc);
        }

        private string GetArmorMdm(int armorInstance)
        {
            if (armorInstance <= 0)
                return null;
            var symbol = _vm.GetSymbolByIndex(armorInstance);
            if (symbol == null || symbol.Type != DaedalusDataType.Instance)
                return null;
            var item = _vm.InitInstance<ItemInstance>(symbol);
            return string.IsNullOrEmpty(item?.VisualChange)
                ? null
                : $"{Path.GetFileNameWithoutExtension(item.VisualChange).ToUpperInvariant()}.MDM";
        }

        public override void _Process(double delta)
        {
            if (_vm == null)
                return;

            _time += delta;
            _frameWatch.Restart();
            var vmBefore = _vmWatch.Elapsed.TotalMilliseconds;

            foreach (var npc in _npcs)
                if (!npc.IsInDialog)
                    Tick(npc, delta);
            Dialog?.Update(delta);
            Fight?.Update();

            var aiMs = _frameWatch.Elapsed.TotalMilliseconds;
            _frameWatch.Restart();
            var camera = GetViewport().GetCamera3D();
            _frameIndex++;
            _animated = 0;
            foreach (var npc in _npcs)
            {
                npc.ClipTime += delta;
                if (AnimationFar <= 0 || camera == null)
                {
                    Animate(npc);
                    continue;
                }

                // Like the engine: NPCs beyond the object distance are hidden and not animated, NPCs outside the view
                // keep their pose, far ones update less often.
                var distance = npc.Position.DistanceTo(camera.GlobalPosition);
                var isVisible = distance < AnimationFar;
                if (npc.Root.Visible != isVisible)
                    npc.Root.Visible = isVisible;
                if (!isVisible || !camera.IsPositionInFrustum(npc.Position + Vector3.Up))
                    continue;
                var interval = distance < 25f ? 1 : distance < 50f ? 2 : 4;
                if ((_frameIndex + npc.GetHashCode()) % interval != 0)
                    continue;
                Animate(npc);
                _animated++;
            }

            var animationMs = _frameWatch.Elapsed.TotalMilliseconds;
            FrameEvents.AiMs += aiMs;
            FrameEvents.AnimationMs += animationMs;
            _boneMs += animationMs;
            _animatedSum += AnimationFar > 0 ? _animated : _npcs.Count;
            _aiMs += aiMs;
            _vmMs += _vmWatch.Elapsed.TotalMilliseconds - vmBefore;
            _frames++;
            _logTime += delta;
            if (_logTime < 1.0)
                return;

            var walking = _npcs.Count(n => n.Current is GothicNpc.GotoAction);
            GD.Print($"[Ai] {_npcs.Count} NPCs ({walking} walking): AI {_aiMs / _frames:F2} ms per frame " +
                     $"(VM {_vmMs / _frames:F2} ms; {_aiMs:F1} / {_vmMs:F1} ms per second), " +
                     $"animation {_boneMs / _frames:F2} ms ({_animatedSum / _frames} animated); per second " +
                     $"{_loops} state loops, {_perceptions} perceptions, {_externals + _defaultExternals} externals " +
                     $"({_defaultExternals} default), {_spoken} lines spoken so far");
            var allocatedTotal = GC.GetAllocatedBytesForCurrentThread();
            GD.Print($"[Ai] allocated per second: {(allocatedTotal - _allocatedAtLastLog) / 1024} KB on the main thread, " +
                     $"{_allocatedInCalls / 1024} KB inside script calls; top: " + string.Join(", ",
                         _allocatedByState.OrderByDescending(a => a.Value).Take(4).Select(a => $"{a.Key} {a.Value / 1024} KB")));
            _allocatedAtLastLog = allocatedTotal;
            _allocatedInCalls = 0;
            _allocatedByState.Clear();
            if (_isCountingDefaults && _defaultByName.Count > 0 && (int)_time % 10 == 0)
                GD.Print("[Ai] unimplemented externals: " + string.Join(", ",
                    _defaultByName.OrderByDescending(d => d.Value).Take(25).Select(d => $"{d.Key} {d.Value}")));
            if (_missingAnis.Count > 0 && _time < 12)
                GD.Print("[Ai] animations not found: " + string.Join(", ",
                    _missingAnis.OrderByDescending(m => m.Value).Take(12).Select(m => $"{m.Key} x{m.Value}")));
            _logTime = 0;
            _aiMs = _vmMs = _boneMs = 0;
            _animatedSum = 0;
            _frames = 0;
            _loops = _perceptions = _externals = _defaultExternals = 0;
        }

        private void Tick(GothicNpc npc, double delta)
        {
            // The dead lie still; their ZS_Dead ran when they died.
            if (npc.IsDead)
                return;

            if (npc.PendingState != 0)
            {
                var next = npc.PendingState;
                npc.PendingState = 0;
                if (npc.IsPendingEndRun && npc.StateEnd != 0 && npc.State is GothicNpc.LoopState.Loop)
                    CallState(npc, npc.StateEnd);
                // The _END may ask for yet another state; the newest request wins. Like Gothic-Unity's StartState,
                // the new state enables its own perceptions (ZS_Attack: no assessing the player mid fight).
                SetState(npc, npc.PendingState != 0 ? npc.PendingState : next);
                npc.PendingState = 0;
                npc.Perceptions.Clear();
            }
            // Active perceptions on the perception timer: the hero is the one the camp reacts to.
            if (_time >= npc.NextPerceptionTime)
            {
                npc.NextPerceptionTime = _time + npc.PerceptionTime;
                if (npc.Perceptions.TryGetValue(_percAssessPlayer, out var assessPlayer) &&
                    npc.Position.DistanceTo(_hero.Position) < 20f)
                {
                    _perceptions++;
                    CallState(npc, assessPlayer, _hero.Instance);
                }
            }

            if (npc.State == GothicNpc.LoopState.Loop)
                npc.StateTime += delta;

            if (!TickAction(npc, delta))
                return;

            if (npc.Actions.Count > 0)
            {
                StartAction(npc, npc.Actions.Dequeue());
                return;
            }

            switch (npc.State)
            {
                case GothicNpc.LoopState.None:
                    StartRoutine(npc);
                    break;
                case GothicNpc.LoopState.Start:
                    if (npc.StateMain == 0)
                        return;
                    CallState(npc, npc.StateMain);
                    npc.State = GothicNpc.LoopState.Loop;
                    npc.NextLoopTime = 0;
                    break;
                case GothicNpc.LoopState.Loop:
                    if (npc.StateLoop == 0)
                    {
                        npc.State = GothicNpc.LoopState.Start;
                        return;
                    }

                    if (_time < npc.NextLoopTime)
                        return;
                    npc.NextLoopTime = _time + LoopInterval(npc);
                    _loops++;
                    FrameEvents.StateLoops++;
                    if (CallState(npc, npc.StateLoop) != _loopContinue && npc.PendingState == 0)
                        npc.State = GothicNpc.LoopState.End;
                    break;
                case GothicNpc.LoopState.End:
                    if (npc.StateEnd != 0)
                        CallState(npc, npc.StateEnd);
                    npc.State = GothicNpc.LoopState.AfterEnd;
                    break;
                case GothicNpc.LoopState.AfterEnd:
                    StartRoutine(npc);
                    break;
            }
        }

        /// <summary>
        /// A game minute passed: NPCs whose routine entry changes end their state (its _END function runs) and then
        /// start the next entry, like the engine's routine manager.
        /// </summary>
        public void SetMinute(int minute)
        {
            if (minute == _minuteOfDay)
                return;
            _minuteOfDay = minute;
            var switched = 0;
            foreach (var npc in _npcs)
            {
                if (npc.Routine.Count == 0 || npc.IsInDialog || npc.State == GothicNpc.LoopState.None)
                    continue;
                var index = ActiveRoutineIndex(npc);
                if (index < 0 || index == npc.RoutineIndex)
                    continue;
                npc.Actions.Clear();
                npc.Current = null;
                npc.State = GothicNpc.LoopState.End;
                // Taken now: an _END function may start another state, and the switch must not repeat each minute.
                npc.RoutineIndex = index;
                switched++;
            }

            if (switched > 0)
                GD.Print($"[Ai] {minute / 60:00}:{minute % 60:00} {switched} NPCs change to their next routine entry");
        }

        private int ActiveRoutineIndex(GothicNpc npc)
        {
            for (var i = 0; i < npc.Routine.Count; i++)
                if (IsActive(npc.Routine[i], _minuteOfDay))
                    return i;
            return -1;
        }

        private void StartRoutine(GothicNpc npc)
        {
            var entry = default(GothicNpc.RoutineEntry);
            var active = ActiveRoutineIndex(npc);
            npc.RoutineIndex = active;
            if (active >= 0)
                entry = npc.Routine[active];

            // Back to the routine: an unconscious NPC got up.
            if (npc.IsDown && !npc.IsDead)
            {
                npc.IsDown = false;
                PlayClip(npc, npc.IdleClip, true);
            }

            if (entry.State == 0 && npc.Routine.Count > 0)
                entry = npc.Routine[0];
            // Monsters have no routine: their start state (ZS_MM_AllScheduler) runs instead.
            if (entry.State == 0 && npc.Instance.StartAiState > 0)
            {
                SetState(npc, npc.Instance.StartAiState);
                return;
            }

            if (entry.State == 0)
            {
                npc.State = GothicNpc.LoopState.Start;
                return;
            }

            npc.Instance.Wp = entry.Waypoint;
            SetState(npc, entry.State);
        }

        private readonly Dictionary<int, (int Loop, int End)> _stateFunctions = new();

        private void SetState(GothicNpc npc, int state)
        {
            if (npc == TracedNpc)
                GD.Print($"[Trace] {_time:F2} {npc.Name}: state {_vm.GetSymbolByIndex(state)?.Name}");
            if (!_stateFunctions.TryGetValue(state, out var functions))
            {
                var symbol = _vm.GetSymbolByIndex(state);
                functions = (_vm.GetSymbolByName($"{symbol.Name}_LOOP")?.Index ?? 0,
                    _vm.GetSymbolByName($"{symbol.Name}_END")?.Index ?? 0);
                _stateFunctions[state] = functions;
            }

            npc.StateMain = state;
            npc.StateLoop = functions.Loop;
            npc.StateEnd = functions.End;
            npc.StateTime = 0;
            npc.State = GothicNpc.LoopState.Start;
        }

        private static bool IsActive(GothicNpc.RoutineEntry entry, int minute) =>
            entry.StartMinute <= entry.EndMinute
                ? minute >= entry.StartMinute && minute < entry.EndMinute
                : minute >= entry.StartMinute || minute < entry.EndMinute;

        private int CallState(GothicNpc npc, int function, NpcInstance other = null)
        {
            _vm.GlobalSelf = npc.Instance;
            _vm.GlobalOther = other ?? _hero.Instance;
            var result = 0;
            var before = _vmWatch.Elapsed.TotalMilliseconds;
            var externalsBefore = _externals + _defaultExternals;
            var gcBefore = GC.CollectionCount(0);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            // Name and return type once per function: a symbol wrapper and its name per call cost more than the call.
            if (!_functions.TryGetValue(function, out var symbol))
            {
                var found = _vm.GetSymbolByIndex(function);
                _functions[function] = symbol = (found?.Name ?? "?", found?.HasReturn ?? false);
            }

            _currentCallFunction = symbol.Name;
            _currentCallNpc = npc;
            _vmWatch.Start();
            try
            {
                if (symbol.HasReturn)
                    result = _vm.Call<int>(function);
                else
                    _vm.Call(function);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Ai] script call failed: {e.Message}");
            }
            finally
            {
                _vmWatch.Stop();
            }

            if (npc == TracedNpc)
                GD.Print($"[Trace] {_time:F2} {npc.Name}: {symbol.Name} -> {result} (other {Npc(_vm.GlobalOther as NpcInstance)?.Name ?? "-"})");
            var callMs = _vmWatch.Elapsed.TotalMilliseconds - before;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            _allocatedByState[symbol.Name] = _allocatedByState.GetValueOrDefault(symbol.Name) + allocated;
            _allocatedInCalls += allocated;
            if (callMs > 2.0 && _slowCallsLogged < 200)
            {
                _slowCallsLogged++;
                GD.Print($"[Ai] slow call at {_time:F1} s {symbol.Name} on {npc.Name}: {callMs:F2} ms, " +
                         $"{_externals + _defaultExternals - externalsBefore} externals, " +
                         $"{GC.CollectionCount(0) - gcBefore} garbage collections during the call");
            }

            return result;
        }

        private void Measure(System.Action call)
        {
            _vmWatch.Start();
            try
            {
                call();
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Ai] script call failed: {e.Message}");
            }
            finally
            {
                _vmWatch.Stop();
            }
        }

        // ---- Actions ----

        private void StartAction(GothicNpc npc, GothicNpc.Action action)
        {
            npc.Current = action;
            npc.ActionTime = 0;
            switch (action)
            {
                case GothicNpc.PlayAniAction play:
                    var isState = play.Name.StartsWith("S_");
                    if (!PlayClip(npc, play.Name, isState))
                        npc.Current = null;
                    else if (isState)
                        npc.Current = null;
                    break;
                case GothicNpc.GotoFpAction goFp:
                    var point = FindFreePoint(npc, goFp.Name, float.MaxValue, goFp.IsNext);
                    if (point == null)
                    {
                        npc.Current = null;
                        break;
                    }

                    // Free the old point, hold the new one (else roaming NPCs run out of free points).
                    if (npc.CurrentFreePoint != null && npc.CurrentFreePoint != point)
                        npc.CurrentFreePoint.Occupant = null;
                    point.Occupant = npc;
                    npc.CurrentFreePoint = point;
                    npc.Path.Clear();
                    npc.Path.AddRange(FindPathTo(npc.Position, point.Position));
                    PlayClip(npc, npc.WalkClip, true);
                    break;
                case AttackAction attack:
                    StartAttack(npc, attack);
                    break;
                case GothicNpc.TurnAction turn:
                    var facing = turn.Target != null
                        ? turn.Target.Position - npc.Position
                        : npc.CurrentFreePoint?.Direction ?? Vector3.Zero;
                    facing.Y = 0;
                    if (facing.LengthSquared() > 0.0001f)
                        npc.Root.Rotation = new Vector3(0, Mathf.Atan2(-facing.X, -facing.Z), 0);
                    npc.Current = null;
                    break;
                case GothicNpc.SpeakAction speak:
                    // Like Gothic-Unity's OutputSvm: SVM_<voice>_<key> names the WAV (and the OU.BIN subtitle). Only
                    // NPCs in hearing range load and play it; decoding runs off the main thread.
                    if (npc.Root?.Visible != true || Sfx == null || npc.Position.DistanceTo(HeroPosition) > _hearingRange)
                    {
                        npc.Current = null;
                        break;
                    }

                    Audio.GothicSound.LoadAsync(_vfs, speak.Name, voice =>
                    {
                        speak.IsLoaded = true;
                        speak.Seconds = voice?.GetLength() ?? 0;
                        if (voice == null || npc.Root == null)
                            return;
                        Sfx.PlayVoice(voice, npc.Position + Vector3.Up * 1.6f);
                        _spoken++;
                    });
                    if (speak.IsOverlay)
                        npc.Current = null;
                    break;
                case GothicNpc.GotoAction go:
                    npc.Path.Clear();
                    npc.Path.AddRange(FindPath(npc.Position, go.Waypoint));
                    if (npc.Path.Count == 0)
                        npc.Current = null;
                    else
                        PlayClip(npc, npc.WalkClip, true);
                    break;
            }
        }

        /// <summary>Returns true when the NPC has no running action.</summary>
        private bool TickAction(GothicNpc npc, double delta)
        {
            if (npc.Current == null)
                return true;

            npc.ActionTime += delta;
            switch (npc.Current)
            {
                case GothicNpc.WaitAction wait:
                    if (npc.ActionTime >= wait.Seconds)
                        npc.Current = null;
                    break;
                case AttackAction attack:
                    if (TickAttack(npc, attack, delta))
                        npc.Current = null;
                    break;
                case GothicNpc.SpeakAction speak:
                    // Waits for the line (and for its decoding, at most a second).
                    if (speak.IsLoaded ? npc.ActionTime >= speak.Seconds : npc.ActionTime > 1.0)
                        npc.Current = null;
                    break;
                case GothicNpc.PlayAniAction:
                    if (!npc.IsClipLooping && npc.ClipTime * npc.Clip.Fps >= npc.Clip.FrameCount - 1)
                    {
                        npc.Current = null;
                        PlayClip(npc, npc.Clip.Next ?? npc.IdleClip, true);
                    }
                    break;
                case GothicNpc.GotoAction or GothicNpc.GotoFpAction:
                    var step = (float)(delta * _walkSpeed);
                    while (step > 0 && npc.Path.Count > 0)
                    {
                        var target = npc.Path[0];
                        var offset = target - npc.Position;
                        var distance = offset.Length();
                        if (distance <= step)
                        {
                            npc.Root.GlobalPosition = target;
                            npc.Path.RemoveAt(0);
                            step -= distance;
                            continue;
                        }

                        var direction = offset / distance;
                        npc.Root.GlobalPosition += direction * step;
                        npc.Root.Rotation = new Vector3(0, Mathf.Atan2(-direction.X, -direction.Z), 0);
                        step = 0;
                    }

                    if (npc.Path.Count == 0)
                    {
                        npc.Current = null;
                        PlayClip(npc, npc.IdleClip, true);
                    }
                    break;
                default:
                    npc.Current = null;
                    break;
            }

            return npc.Current == null;
        }

        private List<Vector3> FindPath(Vector3 from, string targetWaypoint)
        {
            var result = new List<Vector3>();
            if (!_waypoints.TryGetValue(targetWaypoint ?? "", out var targetPosition))
                return result;

            var start = NearestWaypoint(from);
            if (start == null)
                return result;

            // Breadth-first over the waynet, good enough for a camp.
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [start] = null };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current.Equals(targetWaypoint, StringComparison.OrdinalIgnoreCase))
                    break;
                if (!_waynet.TryGetValue(current, out var next))
                    continue;
                foreach (var neighbour in next)
                {
                    if (previous.ContainsKey(neighbour))
                        continue;
                    previous[neighbour] = current;
                    queue.Enqueue(neighbour);
                }
            }

            if (!previous.ContainsKey(targetWaypoint))
            {
                result.Add(targetPosition);
                return result;
            }

            for (var node = targetWaypoint; node != null; node = previous[node])
                result.Insert(0, _waypoints[node]);
            return result;
        }

        /// <summary>Over the waynet to the waypoint nearest the place, then straight to it (freepoints lie off the net).</summary>
        private List<Vector3> FindPathTo(Vector3 from, Vector3 to)
        {
            var last = NearestWaypoint(to);
            var path = last != null && _waypoints[last].DistanceTo(to) < from.DistanceTo(to)
                ? FindPath(from, last)
                : new List<Vector3>();
            path.Add(to);
            return path;
        }

        private string NearestWaypoint(Vector3 position)
        {
            string best = null;
            var bestDistance = float.MaxValue;
            foreach (var (name, point) in _waypoints)
            {
                var distance = point.DistanceSquaredTo(position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = name;
                }
            }

            return best;
        }

        // ---- Animation ----

        /// <summary>Body, armour and its mesh, for test logs.</summary>
        public string DescribeLook(GothicNpc npc) =>
            $"body {npc.Body}, head {npc.Head}, armour {(npc.ArmorInstance > 0 ? _vm.GetSymbolByIndex(npc.ArmorInstance)?.Name : "none")} " +
            $"({GetArmorMdm(npc.ArmorInstance) ?? "naked"})";

        /// <summary>
        /// The hero uses an item (eats, drinks): its on_state[0] function runs with self = the hero, item = the item,
        /// like Gothic-Unity's AI_UseItem. Returns false for items without a use function.
        /// </summary>
        public bool UseItem(string instance)
        {
            var symbol = _vm.GetSymbolByName(instance);
            if (symbol is not { Type: DaedalusDataType.Instance } || _hero?.Instance == null)
                return false;
            var item = _vm.InitInstance<ItemInstance>(symbol);
            var use = item?.GetOnState(ItemStateSlot.Slot0) ?? 0;
            if (use <= 0)
                return false;

            var self = _vm.GlobalSelf;
            var oldItem = _vm.GlobalItem;
            _vm.GlobalSelf = _hero.Instance;
            _vm.GlobalItem = item;
            try
            {
                _vm.Call(use);
                return true;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Ai] use function of {instance} failed: {e.Message}");
                return false;
            }
            finally
            {
                _vm.GlobalSelf = self;
                _vm.GlobalItem = oldItem;
            }
        }

        /// <summary>Food and potions: what the hero eats or drinks at his mouth (ITEM_KAT_FOOD, ITEM_KAT_POTIONS).</summary>
        public bool IsConsumable(string instance)
        {
            var symbol = _vm.GetSymbolByName(instance);
            if (symbol is not { Type: DaedalusDataType.Instance })
                return false;
            _consumableFlags ??= (_vm.GetSymbolByName("ITEM_KAT_FOOD")?.GetInt(0, null) ?? 0) |
                                 (_vm.GetSymbolByName("ITEM_KAT_POTIONS")?.GetInt(0, null) ?? 0);
            var item = _vm.InitInstance<ItemInstance>(symbol);
            return item != null && (item.MainFlag & _consumableFlags.Value) != 0;
        }

        private int? _consumableFlags;
        private int _heroChangesLogged;
        private long _spoken;
        private int _svmCalls;
        private const float _hearingRange = 30f;
        private readonly Dictionary<string, int> _defaultByName = new();
        private bool _isCountingDefaults = OS.GetCmdlineUserArgs().Contains("--count-externals");
        private string _currentCallFunction;
        private GothicNpc _currentCallNpc;
        private readonly Dictionary<int, (string Name, bool HasReturn)> _functions = new();

        /// <summary>A script constant (e.g. NPC_TALENT_PICKLOCK), or the fallback if the scripts have none.</summary>
        public int GetConstant(string name, int fallback) => _vm.GetSymbolByName(name)?.GetInt(0, null) ?? fallback;

        /// <summary>
        /// Calls a script function with self = the hero (G_PickLock(success, brokenOpen) etc.); false if the scripts
        /// have none - calling an external would crash the VM.
        /// </summary>
        public bool CallAsHero(string function, params int[] args)
        {
            var symbol = _vm.GetSymbolByName(function);
            if (symbol is not { Type: DaedalusDataType.Function, IsExternal: false } || _hero?.Instance == null)
                return false;
            var self = _vm.GlobalSelf;
            _vm.GlobalSelf = _hero.Instance;
            try
            {
                switch (args.Length)
                {
                    case 0: _vm.Call(symbol.Index); break;
                    case 1: _vm.Call(symbol.Index, args[0]); break;
                    default: _vm.Call(symbol.Index, args[0], args[1]); break;
                }

                return true;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Ai] {function} failed: {e.Message}");
                return false;
            }
            finally
            {
                _vm.GlobalSelf = self;
            }
        }

        public bool HasClip(string name) => _factory.GetClip(name.ToUpperInvariant()) != null;

        public bool HasClip(GothicNpc npc, string name) => (npc.Factory ?? _factory).GetClip(name.ToUpperInvariant()) != null;

        /// <summary>A script reaction now (a perception's function), self = the NPC, other = whom it reacts to.</summary>
        public void RunState(GothicNpc npc, int function, NpcInstance other) => CallState(npc, function, other);

        /// <summary>The engine starts a state at once (ZS_Unconscious, ZS_Dead): the queue is dropped, main runs next.</summary>
        public void StartStateNow(GothicNpc npc, int state, NpcInstance other)
        {
            npc.PendingState = 0;
            npc.Perceptions.Clear();
            npc.Actions.Clear();
            npc.Current = null;
            SetState(npc, state);
            CallState(npc, state, other);
            npc.State = GothicNpc.LoopState.Loop;
            npc.NextLoopTime = 0;
        }

        public bool PlayClip(GothicNpc npc, string name, bool isLooping)
        {
            var clip = (npc.Factory ?? _factory).GetClip(name.ToUpperInvariant());
            if (clip == null)
            {
                _missingAnis[name] = _missingAnis.GetValueOrDefault(name) + 1;
                return false;
            }

            npc.Clip = clip;
            npc.ClipTime = 0;
            npc.IsClipLooping = isLooping;
            return true;
        }

        /// <summary>"split": position and rotation call per bone, "pose": one SetBonePose call per bone.</summary>
        public string BoneMode { get; set; } = "split";

        /// <summary>NPCs further than this (metres) are hidden and not animated; 0 animates all.</summary>
        public float AnimationFar { get; set; }

        private long _frameIndex;
        private int _animated;
        private long _animatedSum;

        private void Animate(GothicNpc npc)
        {
            npc.Clip?.Apply(npc.Skeleton, npc.ClipTime, npc.IsClipLooping, BoneMode == "pose");
        }

        // ---- Externals ----

        private GothicNpc Npc(DaedalusInstance instance) => instance?.UserData as GothicNpc;

        private int DetectNpc(NpcInstance npc, int instance, int state, int guild, bool isPlayerDetected)
        {
            _externals++;
            if (Npc(npc) is not { Root: not null } self)
                return 0;
            var range = npc.SensesRange / 100f;
            GothicNpc best = null;
            var bestDistance = range;
            // No LINQ: called by many states every loop, it must not make garbage.
            for (var i = 0; i <= _npcs.Count; i++)
            {
                var other = i < _npcs.Count ? _npcs[i] : _hero;
                if (other == self || other?.Root == null || other.Instance == null)
                    continue;
                if (!isPlayerDetected && other == _hero || instance >= 0 && other.Instance.Index != instance)
                    continue;
                if (state >= 0 && other.StateMain != state || guild >= 0 && other.Instance.Guild != guild)
                    continue;
                var distance = other.Position.DistanceTo(self.Position);
                if (distance >= bestDistance)
                    continue;
                best = other;
                bestDistance = distance;
            }

            if (best == null)
                return 0;
            _vm.GlobalOther = best.Instance;
            return 1;
        }

        private void RegisterExternals()
        {
            _vm.RegisterExternalDefault((_, symbol) =>
            {
                _defaultExternals++;
                if (_isCountingDefaults)
                    _defaultByName[symbol.Name] = _defaultByName.GetValueOrDefault(symbol.Name) + 1;
            });

            _vm.RegisterExternal("Wld_InsertNpc", (DaedalusVm.ExternalFuncV<int, string>)((instance, waypoint) =>
            {
                _externals++;
                Insert(instance, waypoint);
            }));
            // At a freepoint or a waypoint by name, like the engine.
            _vm.RegisterExternal("Wld_InsertItem", (DaedalusVm.ExternalFuncV<int, string>)((item, spawn) =>
            {
                _externals++;
                var name = _vm.GetSymbolByIndex(item)?.Name;
                var point = _freePoints.Find(p => string.Equals(p.Name, spawn, StringComparison.OrdinalIgnoreCase));
                Vector3 position;
                if (point != null)
                    position = point.Position;
                else if (!_waypoints.TryGetValue(spawn ?? "", out position))
                    return;
                if (name == null)
                    return;
                InsertedItems.Add((name, position));
                ItemInserted?.Invoke(name, position);
            }));
            _vm.RegisterExternal("Mdl_SetVisual", (DaedalusVm.ExternalFuncV<NpcInstance, string>)((npc, visual) =>
            {
                _externals++;
                if (Npc(npc) is { } target)
                    target.Visual = visual;
            }));
            _vm.RegisterExternal("Mdl_SetVisualBody",
                (DaedalusVm.ExternalFuncV<NpcInstance, string, int, int, string, int, int, int>)(
                    (npc, body, bodyTex, skin, head, headTex, teeth, armor) =>
                    {
                        _externals++;
                        if (Npc(npc) is not { } target)
                            return;
                        target.Body = body;
                        target.BodyTexture = bodyTex;
                        target.SkinColor = skin;
                        target.Head = head;
                        target.HeadTexture = headTex;
                        target.ArmorInstance = armor;
                    }));
            _vm.RegisterExternal("TA_MIN",
                (DaedalusVm.ExternalFuncV<NpcInstance, int, int, int, int, int, string>)(
                    (npc, startH, startM, stopH, stopM, action, waypoint) =>
                    {
                        _externals++;
                        Npc(npc)?.Routine.Add(new GothicNpc.RoutineEntry
                        {
                            StartMinute = startH * 60 + startM,
                            EndMinute = stopH * 60 + stopM,
                            State = action,
                            Waypoint = waypoint
                        });
                    }));
            _vm.RegisterExternal("Npc_PercEnable", (DaedalusVm.ExternalFuncV<NpcInstance, int, int>)((npc, perc, function) =>
            {
                _externals++;
                if (Npc(npc) is { } target)
                    target.Perceptions[perc] = function;
            }));
            _vm.RegisterExternal("Npc_SetPercTime", (DaedalusVm.ExternalFuncV<NpcInstance, float>)((npc, seconds) =>
            {
                _externals++;
                if (Npc(npc) is { } target)
                    target.PerceptionTime = Mathf.Max(0.2f, seconds);
            }));
            _vm.RegisterExternal("AI_PlayAni", (DaedalusVm.ExternalFuncV<NpcInstance, string>)((npc, ani) =>
            {
                _externals++;
                Npc(npc)?.Actions.Enqueue(new GothicNpc.PlayAniAction { Name = ani.ToUpperInvariant() });
            }));
            _vm.RegisterExternal("AI_Wait", (DaedalusVm.ExternalFuncV<NpcInstance, float>)((npc, seconds) =>
            {
                _externals++;
                Npc(npc)?.Actions.Enqueue(new GothicNpc.WaitAction { Seconds = seconds });
            }));
            _vm.RegisterExternal("AI_GotoWP", (DaedalusVm.ExternalFuncV<NpcInstance, string>)((npc, waypoint) =>
            {
                _externals++;
                Npc(npc)?.Actions.Enqueue(new GothicNpc.GotoAction { Waypoint = waypoint });
            }));
            _vm.RegisterExternal("AI_StartState", (DaedalusVm.ExternalFuncV<NpcInstance, int, int, string>)(
                (npc, function, behaviour, waypoint) =>
                {
                    _externals++;
                    if (Npc(npc) is not { } target || function <= 0)
                        return;
                    if (!string.IsNullOrEmpty(waypoint))
                        target.Instance.Wp = waypoint;
                    // The switch happens after the running script call (often the old state's loop, which then
                    // returns LOOP_END): the new state wins over that end, and behaviour 1 runs the old _END first.
                    target.PendingState = function;
                    target.IsPendingEndRun = behaviour != 0;
                }));
            _vm.RegisterExternal("Npc_GetDistToWP", (DaedalusVm.ExternalFunc<int, NpcInstance, string>)((npc, waypoint) =>
            {
                _externals++;
                if (Npc(npc)?.Root == null || !_waypoints.TryGetValue(waypoint ?? "", out var point))
                    return int.MaxValue;
                return (int)(Npc(npc).Position.DistanceTo(point) * 100f);
            }));
            _vm.RegisterExternal("Npc_GetDistToNpc",
                (DaedalusVm.ExternalFunc<int, DaedalusInstance, DaedalusInstance>)((a, b) =>
                {
                    _externals++;
                    var first = Npc(a);
                    var second = Npc(b);
                    if (first?.Root == null || second?.Root == null)
                        return int.MaxValue;
                    return (int)(first.Position.DistanceTo(second.Position) * 100f);
                }));
            _vm.RegisterExternal("Npc_GetStateTime", (DaedalusVm.ExternalFunc<int, NpcInstance>)(npc =>
            {
                _externals++;
                return (int)(Npc(npc)?.StateTime ?? 0);
            }));
            _vm.RegisterExternal("Npc_SetStateTime", (DaedalusVm.ExternalFuncV<NpcInstance, int>)((npc, seconds) =>
            {
                _externals++;
                if (Npc(npc) is { } target)
                    target.StateTime = seconds;
            }));
            _vm.RegisterExternal("Npc_IsInState", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((npc, state) =>
            {
                _externals++;
                return Npc(npc)?.StateMain == state ? 1 : 0;
            }));
            _vm.RegisterExternal("Wld_IsTime", (DaedalusVm.ExternalFunc<int, int, int, int, int>)((h1, m1, h2, m2) =>
            {
                _externals++;
                return IsActive(new GothicNpc.RoutineEntry { StartMinute = h1 * 60 + m1, EndMinute = h2 * 60 + m2 },
                    _minuteOfDay) ? 1 : 0;
            }));
            _vm.RegisterExternal("Hlp_Random", (DaedalusVm.ExternalFunc<int, int>)(max =>
            {
                _externals++;
                return max > 0 ? _random.Next(max) : 0;
            }));
            _vm.RegisterExternal("Hlp_GetNpc", (DaedalusVm.ExternalFunc<NpcInstance, int>)(instance =>
            {
                _externals++;
                if (_npcBySymbol.TryGetValue(instance, out var npc))
                    return npc.Instance;
                // The hero by his own instance; anyone else not inserted is no NPC (not the hero, as before).
                return _hero?.Instance != null && instance == _hero.Instance.Index ? _hero.Instance : null;
            }));
            // Attributes live in the NpcInstance; HP and mana stay between 0 and their max (Gothic-Unity's rule).
            _vm.RegisterExternal("Npc_ChangeAttribute", (DaedalusVm.ExternalFuncV<NpcInstance, int, int>)((npc, attribute, value) =>
            {
                _externals++;
                if (npc == null || attribute < 0 || attribute > (int)NpcAttribute.RegenerateMana)
                    return;
                var newValue = npc.GetAttribute((NpcAttribute)attribute) + value;
                if (attribute is (int)NpcAttribute.HitPoints or (int)NpcAttribute.Mana)
                {
                    var max = npc.GetAttribute((NpcAttribute)(attribute + 1));
                    if (max > 0)
                        newValue = Math.Clamp(newValue, 0, max);
                }

                if (_hero?.Instance != null && npc.Index == _hero.Instance.Index && _heroChangesLogged++ < 10)
                    GD.Print($"[Ai] hero attribute {(NpcAttribute)attribute} {value:+#;-#;0} -> {newValue} in {_currentCallFunction ?? "-"} of {_currentCallNpc?.Name ?? "-"}");
                npc.SetAttribute((NpcAttribute)attribute, newValue);
            }));
            // Freepoints (Gothic-Unity's rules: looked up within 7 m, held while an NPC walks to or stands on one).
            _vm.RegisterExternal("Wld_IsFPAvailable", (DaedalusVm.ExternalFunc<int, NpcInstance, string>)((npc, name) =>
            {
                _externals++;
                return Npc(npc) is { Root: not null } target && FindFreePoint(target, name, _freePointLookup, false) != null
                    ? 1
                    : 0;
            }));
            _vm.RegisterExternal("Wld_IsNextFPAvailable", (DaedalusVm.ExternalFunc<int, NpcInstance, string>)((npc, name) =>
            {
                _externals++;
                return Npc(npc) is { Root: not null } target && FindFreePoint(target, name, _freePointLookup, true) != null
                    ? 1
                    : 0;
            }));
            _vm.RegisterExternal("Npc_IsOnFP", (DaedalusVm.ExternalFunc<int, NpcInstance, string>)((npc, name) =>
            {
                _externals++;
                var point = Npc(npc)?.CurrentFreePoint;
                return point != null && point.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 &&
                       point.Position.DistanceTo(Npc(npc).Position) < 1f
                    ? 1
                    : 0;
            }));
            _vm.RegisterExternal("AI_GotoFP", (DaedalusVm.ExternalFuncV<NpcInstance, string>)((npc, name) =>
            {
                _externals++;
                Npc(npc)?.Actions.Enqueue(new GothicNpc.GotoFpAction { Name = name });
            }));
            if (_vm.GetSymbolByName("AI_GotoNextFP") != null)
                _vm.RegisterExternal("AI_GotoNextFP", (DaedalusVm.ExternalFuncV<NpcInstance, string>)((npc, name) =>
                {
                    _externals++;
                    Npc(npc)?.Actions.Enqueue(new GothicNpc.GotoFpAction { Name = name, IsNext = true });
                }));
            _vm.RegisterExternal("AI_AlignToFP", (DaedalusVm.ExternalFuncV<NpcInstance>)(npc =>
            {
                _externals++;
                Npc(npc)?.Actions.Enqueue(new GothicNpc.TurnAction());
            }));
            _vm.RegisterExternal("AI_TurnToNpc", (DaedalusVm.ExternalFuncV<NpcInstance, NpcInstance>)((npc, other) =>
            {
                _externals++;
                if (Npc(other) is { Root: not null } target)
                    Npc(npc)?.Actions.Enqueue(new GothicNpc.TurnAction { Target = target });
            }));
            // Another NPC (or the hero) within the senses range by instance, AI state and guild (-1: any); becomes other.
            _vm.RegisterExternal("Wld_DetectNpc", (DaedalusVm.ExternalFunc<int, NpcInstance, int, int, int>)(
                (npc, instance, state, guild) => DetectNpc(npc, instance, state, guild, true)));
            if (_vm.GetSymbolByName("Wld_DetectNpcEx") != null)
                _vm.RegisterExternal("Wld_DetectNpcEx", (DaedalusVm.ExternalFunc<int, NpcInstance, int, int, int, int>)(
                    (npc, instance, state, guild, player) => DetectNpc(npc, instance, state, guild, player != 0)));

            // The voices' standard lines: greetings, smalltalk, warnings (SVM_<voice>_<key>).
            foreach (var (external, isOverlay) in new[] { ("AI_OutputSVM", false), ("AI_OutputSVM_Overlay", true) })
            {
                if (_vm.GetSymbolByName(external) == null)
                    continue;
                _vm.RegisterExternal(external, (DaedalusVm.ExternalFuncV<NpcInstance, NpcInstance, string>)((self, target, key) =>
                {
                    _externals++;
                    if (Npc(self) is not { } speaker || speaker == _hero || string.IsNullOrEmpty(key))
                        return;
                    speaker.Actions.Enqueue(new GothicNpc.SpeakAction
                    {
                        Name = $"SVM_{self.Voice}_{key.TrimStart('$')}", IsOverlay = isOverlay
                    });
                }));
            }

            _vm.RegisterExternal("Snd_Play", (DaedalusVm.ExternalFuncV<string>)(name =>
            {
                _externals++;
                Sfx?.Play(name);
            }));
            if (_vm.GetSymbolByName("Snd_Play3d") != null)
                _vm.RegisterExternal("Snd_Play3d", (DaedalusVm.ExternalFuncV<NpcInstance, string>)((npc, name) =>
                {
                    _externals++;
                    var target = Npc(npc);
                    if (target?.Root != null)
                        Sfx?.Play3d(name, target.Position + Vector3.Up * 1.5f);
                }));

            // Talents (lock picking, weapons): value = G1's chance to break a lock pick etc., skill = the level.
            _vm.RegisterExternal("Npc_SetTalentValue", (DaedalusVm.ExternalFuncV<NpcInstance, int, int>)((npc, talent, value) =>
            {
                _externals++;
                if (Npc(npc) is { } target)
                    target.TalentValues[talent] = value;
            }));
            _vm.RegisterExternal("Npc_SetTalentSkill", (DaedalusVm.ExternalFuncV<NpcInstance, int, int>)((npc, talent, skill) =>
            {
                _externals++;
                if (Npc(npc) is { } target)
                    target.TalentSkills[talent] = skill;
            }));
            _vm.RegisterExternal("Npc_GetTalentValue", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((npc, talent) =>
            {
                _externals++;
                return Npc(npc)?.TalentValues.GetValueOrDefault(talent) ?? 0;
            }));
            _vm.RegisterExternal("Npc_GetTalentSkill", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((npc, talent) =>
            {
                _externals++;
                return Npc(npc)?.TalentSkills.GetValueOrDefault(talent) ?? 0;
            }));

            // Inventories (Gothic-Unity's VmExternalDomain signatures): scripts fill them on insert and in dialogs.
            _vm.RegisterExternal("CreateInvItem", (DaedalusVm.ExternalFuncV<NpcInstance, int>)((npc, item) =>
            {
                _externals++;
                Npc(npc)?.Inventory.Add(_vm.GetSymbolByIndex(item)?.Name);
            }));
            _vm.RegisterExternal("CreateInvItems", (DaedalusVm.ExternalFuncV<NpcInstance, int, int>)((npc, item, amount) =>
            {
                _externals++;
                Npc(npc)?.Inventory.Add(_vm.GetSymbolByIndex(item)?.Name, amount);
            }));
            _vm.RegisterExternal("Npc_RemoveInvItem", (DaedalusVm.ExternalFuncV<NpcInstance, int>)((npc, item) =>
            {
                _externals++;
                Npc(npc)?.Inventory.Remove(_vm.GetSymbolByIndex(item)?.Name);
            }));
            _vm.RegisterExternal("Npc_RemoveInvItems", (DaedalusVm.ExternalFuncV<NpcInstance, int, int>)((npc, item, amount) =>
            {
                _externals++;
                Npc(npc)?.Inventory.Remove(_vm.GetSymbolByIndex(item)?.Name, amount);
            }));
            _vm.RegisterExternal("Npc_HasItems", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((npc, item) =>
            {
                _externals++;
                var name = _vm.GetSymbolByIndex(item)?.Name;
                return name != null ? Npc(npc)?.Inventory[name] ?? 0 : 0;
            }));
            _vm.RegisterExternal("Hlp_IsValidNpc", (DaedalusVm.ExternalFunc<int, NpcInstance>)(npc =>
            {
                _externals++;
                return npc != null ? 1 : 0;
            }));

            RegisterFightExternals();
        }
    }
}
