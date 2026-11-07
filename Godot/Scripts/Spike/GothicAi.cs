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
        private HumanFactory _factory;
        private ZenKit.World _world;
        private GroundLightSampler _light;
        private readonly List<GothicNpc> _npcs = new();
        private readonly Dictionary<int, GothicNpc> _npcBySymbol = new();
        private readonly List<(int Instance, string Waypoint)> _spawns = new();
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

        /// <summary>The player becomes the hero the NPCs perceive (instead of the fixed hero position).</summary>
        public void SetHero(Node3D player)
        {
            if (_hero == null)
                return;
            _hero.Root.QueueFree();
            _hero.Root = player;
        }

        public void Init(Vfs vfs, HumanFactory factory, ZenKit.World world, GroundLightSampler light, int hour,
            Vector3 heroPosition, float radius, string startupFunction)
        {
            _factory = factory;
            _world = world;
            _light = light;
            _minuteOfDay = hour * 60;

            foreach (var point in world.WayNet.Points)
                _waypoints[point.Name] = MeshPart.ToGodot(point.Position);
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

            var startup = _vm.GetSymbolByName(startupFunction);
            if (startup != null)
                Measure(() => _vm.Call(startup.Index));

            var center = heroPosition;
            foreach (var (instance, waypoint) in _spawns)
            {
                if (!_waypoints.TryGetValue(waypoint ?? "", out var spawnPosition) ||
                    spawnPosition.DistanceTo(center) > radius)
                    continue;
                Spawn(instance, waypoint);
            }

            _world = null;
            GD.Print($"[Ai] {startupFunction} inserted {_spawns.Count} NPCs, {_npcs.Count} within {radius} m spawned at " +
                     $"{hour:00}:00 ({_skippedMonsters} monsters left out), routines {_npcs.Sum(n => n.Routine.Count)} entries");
        }

        private void AddEdge(string from, string to)
        {
            if (!_waynet.TryGetValue(from, out var list))
                _waynet[from] = list = new List<string>();
            list.Add(to);
        }

        private void Spawn(int instanceIndex, string waypoint)
        {
            var symbol = _vm.GetSymbolByIndex(instanceIndex);
            if (symbol == null || symbol.Type != DaedalusDataType.Instance)
                return;

            var npc = new GothicNpc { SpawnWaypoint = waypoint };
            var instance = _vm.AllocInstance<NpcInstance>(symbol);
            instance.UserData = npc;
            npc.Instance = instance;
            Measure(() => _vm.InitInstance(instance));
            npc.Name = instance.GetName(NpcNameSlot.Slot0);

            // Daily routine: Rtn_* calls TA_* which call TA_Min(self, ...).
            if (instance.DailyRoutine > 0)
            {
                _vm.GlobalSelf = instance;
                Measure(() => _vm.Call(instance.DailyRoutine));
            }

            // Only humans in this test: monsters (G2 inserts sheep, wolves, ...) have their own skeletons.
            if (npc.Visual != null && !npc.Visual.StartsWith("HUMANS", StringComparison.OrdinalIgnoreCase))
            {
                _skippedMonsters++;
                return;
            }

            var armorMdm = GetArmorMdm(npc.ArmorInstance);
            var body = _factory.GetSkinnedMesh(armorMdm ?? $"{npc.Body.ToUpperInvariant()}.MDM") ??
                       _factory.GetSkinnedMesh("HUM_BODY_NAKED0.MDM");
            if (body == null)
                return;

            npc.Root = new Node3D { Name = $"{symbol.Name}" };
            AddChild(npc.Root);
            // Like the engine on world start: at the waypoint of the routine entry that runs now.
            var current = npc.Routine.FirstOrDefault(r => IsActive(r, _minuteOfDay));
            npc.Root.GlobalPosition = current.Waypoint != null && _waypoints.TryGetValue(current.Waypoint, out var routinePosition)
                ? routinePosition
                : _waypoints[waypoint];
            npc.Skeleton = _factory.CreateSkeleton();
            npc.Root.AddChild(npc.Skeleton);
            var tint = _light.Sample(npc.Root.GlobalPosition, new Color(0.6f, 0.6f, 0.6f));
            var bodyInstance = new MeshInstance3D { Mesh = body, Skin = _factory.Skin };
            npc.Skeleton.AddChild(bodyInstance);
            bodyInstance.Skeleton = bodyInstance.GetPathTo(npc.Skeleton);
            bodyInstance.SetInstanceShaderParameter("tint", tint);

            var head = string.IsNullOrEmpty(npc.Head) ? null : _factory.GetHeadMesh($"{npc.Head.ToUpperInvariant()}.MMB");
            if (head != null)
            {
                var attachment = new BoneAttachment3D { BoneName = "BIP01 HEAD" };
                npc.Skeleton.AddChild(attachment);
                var headInstance = new MeshInstance3D { Mesh = head };
                attachment.AddChild(headInstance);
                headInstance.SetInstanceShaderParameter("tint", tint);
            }

            PlayClip(npc, "S_RUN", true);
            npc.NextPerceptionTime = _random.NextDouble() * 2.0;
            _npcs.Add(npc);
            _npcBySymbol[instanceIndex] = npc;
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
                Tick(npc, delta);

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
                     $"({_defaultExternals} default)");
            var allocatedTotal = GC.GetAllocatedBytesForCurrentThread();
            GD.Print($"[Ai] allocated per second: {(allocatedTotal - _allocatedAtLastLog) / 1024} KB on the main thread, " +
                     $"{_allocatedInCalls / 1024} KB inside script calls; top: " + string.Join(", ",
                         _allocatedByState.OrderByDescending(a => a.Value).Take(4).Select(a => $"{a.Key} {a.Value / 1024} KB")));
            _allocatedAtLastLog = allocatedTotal;
            _allocatedInCalls = 0;
            _allocatedByState.Clear();
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
                    npc.NextLoopTime = _time + npc.PerceptionTime;
                    _loops++;
                    FrameEvents.StateLoops++;
                    if (CallState(npc, npc.StateLoop) != _loopContinue)
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

        private void StartRoutine(GothicNpc npc)
        {
            var entry = default(GothicNpc.RoutineEntry);
            foreach (var candidate in npc.Routine)
            {
                if (IsActive(candidate, _minuteOfDay))
                {
                    entry = candidate;
                    break;
                }
            }

            if (entry.State == 0 && npc.Routine.Count > 0)
                entry = npc.Routine[0];
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
            var symbol = _vm.GetSymbolByIndex(function);
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
                case GothicNpc.GotoAction go:
                    npc.Path.Clear();
                    npc.Path.AddRange(FindPath(npc.Position, go.Waypoint));
                    if (npc.Path.Count == 0)
                        npc.Current = null;
                    else
                        PlayClip(npc, "S_WALKL", true);
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
                case GothicNpc.PlayAniAction:
                    if (!npc.IsClipLooping && npc.ClipTime * npc.Clip.Fps >= npc.Clip.FrameCount - 1)
                    {
                        npc.Current = null;
                        PlayClip(npc, npc.Clip.Next ?? "S_RUN", true);
                    }
                    break;
                case GothicNpc.GotoAction:
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
                        PlayClip(npc, "S_RUN", true);
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

        private bool PlayClip(GothicNpc npc, string name, bool isLooping)
        {
            var clip = _factory.GetClip(name.ToUpperInvariant());
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
            var clip = npc.Clip;
            if (clip == null)
                return;

            var t = npc.ClipTime * clip.Fps;
            var last = clip.FrameCount - 1;
            if (npc.IsClipLooping)
                t %= clip.FrameCount;
            else if (t > last)
                t = last;

            var frame0 = (int)t;
            var frame1 = npc.IsClipLooping ? (frame0 + 1) % clip.FrameCount : Math.Min(frame0 + 1, last);
            var weight = (float)(t - frame0);
            var channels = clip.Nodes.Length;
            for (var channel = 0; channel < channels; channel++)
            {
                var a = frame0 * channels + channel;
                var b = frame1 * channels + channel;
                var bone = clip.Nodes[channel];
                var position = clip.Positions[a].Lerp(clip.Positions[b], weight);
                var rotation = clip.Rotations[a].Slerp(clip.Rotations[b], weight);
                if (BoneMode == "pose")
                {
                    npc.Skeleton.SetBonePose(bone, new Transform3D(new Basis(rotation), position));
                }
                else
                {
                    npc.Skeleton.SetBonePosePosition(bone, position);
                    npc.Skeleton.SetBonePoseRotation(bone, rotation);
                }
            }
        }

        // ---- Externals ----

        private GothicNpc Npc(DaedalusInstance instance) => instance?.UserData as GothicNpc;

        private void RegisterExternals()
        {
            _vm.RegisterExternalDefault((_, _) => _defaultExternals++);

            _vm.RegisterExternal("Wld_InsertNpc", (DaedalusVm.ExternalFuncV<int, string>)((instance, waypoint) =>
            {
                _externals++;
                _spawns.Add((instance, waypoint));
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
                        target.Head = head;
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
                    SetState(target, function);
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
                return _hero?.Instance;
            }));
            _vm.RegisterExternal("Hlp_IsValidNpc", (DaedalusVm.ExternalFunc<int, NpcInstance>)(npc =>
            {
                _externals++;
                return npc != null ? 1 : 0;
            }));
        }
    }
}
