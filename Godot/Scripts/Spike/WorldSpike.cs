using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using ZenKit;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Spike scene: loads a Gothic world through ZenKit, builds it in Godot and turns the camera in the Old Camp
    /// for repeatable frame measurements (desktop or OpenXR on the Quest).
    /// </summary>
    public partial class WorldSpike : Node3D
    {
        private const float _turnDegreesPerSecond = 18f;

        private SpikeArgs _args;
        private Node3D _rig;
        private double _time;
        private bool _isScreenshotTaken;
        private Camera3D _camera;
        private string[] _shots = Array.Empty<string>();
        private int _shotIndex = -1;
        private double _shotTime;
        private bool _isShotPending;
        private HumanCrowd _crowd;
        private GothicAi _ai;
        private AudioEffectRecord _recorder;
        private VobBuilder _vobs;
        private HumanFactory _humans;
        private ArrayMesh _handMesh;

        public override void _Ready()
        {
            _args = new SpikeArgs();
            NativeLibraries.Register();
            AddChild(new GothicGodot.Port.GothicCoreHost { Name = "GothicCore" });
            AddChild(new PerfLog());

            var isXr = TryStartXr();
            ApplyRenderOptions();
            var watch = Stopwatch.StartNew();

            var vfs = new Vfs();
            var dataPath = Path.Combine(_args.DataPath, "Data");
            foreach (var file in Directory.GetFiles(dataPath)
                         .Where(f => f.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase)))
                vfs.MountDisk(file, VfsOverwriteBehavior.Older);
            var workPath = Path.Combine(_args.DataPath, "_work");
            if (Directory.Exists(workPath))
                vfs.Mount(Path.GetFullPath(workPath), "/_work", VfsOverwriteBehavior.Older);
            Log($"mounted {dataPath} in {watch.ElapsedMilliseconds} ms");

            var vmBenchMs = _args.GetFloat("vm-bench", 0f);
            if (vmBenchMs > 0)
            {
                VmBench.Run(vfs, vmBenchMs);
                if (_args.Get("vm-only", "false") == "true")
                {
                    GetTree().Quit();
                    return;
                }
            }

            var world = new ZenKit.World(vfs, _args.World, _args.IsGothic2 ? GameVersion.Gothic2 : GameVersion.Gothic1);
            // Not Cache(): that copies the whole mesh incl. its light maps into managed memory (G2: ~4.5 GB peak).
            var mesh = world.Mesh;
            Log($"world {_args.World} loaded in {watch.ElapsedMilliseconds} ms: {mesh.PolygonCount} polygons, managed {GC.GetTotalMemory(false) / 1048576} MB");

            var textures = new TextureArrayBuilder(vfs, _args.SliceSize);
            var materials = CreateMaterials();
            var worldRoot = new Node3D { Name = "World" };
            AddChild(worldRoot);

            var groundLight = new GroundLightSampler();
            var builder = new WorldChunkBuilder(_args.CellSize)
            {
                ObjectFarDistance = _args.GetFloat("object-far", 0f),
                OccluderMinArea = _args.GetFloat("occluder-area", 0f)
            };
            if (builder.OccluderMinArea > 0)
                GetViewport().UseOcclusionCulling = true;
            builder.AddWorld(mesh, WorldChunkBuilder.GetLeafPolygons(world.BspTree.Cache()), textures, groundLight);
            // The cached ZenKit mesh is the biggest managed object graph (G2: several hundred MB); free it now.
            mesh = null;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Log($"world polygons collected in {watch.ElapsedMilliseconds} ms, managed {GC.GetTotalMemory(false) / 1048576} MB");

            // --vob-mode=nodes (one node per object), merged (objects in the world cells) or none.
            var vobMode = _args.IsVobsEnabled ? _args.Get("vob-mode", "merged") : "none";
            if (vobMode != "none")
            {
                var vobs = new VobBuilder(vfs, textures, groundLight)
                {
                    SeparateItems = _args.Get("items", "true") == "true"
                };
                if (vobMode == "nodes")
                {
                    var vobRoot = new Node3D { Name = "Vobs" };
                    AddChild(vobRoot);
                    vobs.BuildNodes(world.RootObjects, vobRoot, materials[3], materials[4]);
                }
                else
                {
                    vobs.BuildMerged(world.RootObjects, builder);
                }

                _vobs = vobs;

                Log($"objects ({vobMode}) built in {watch.ElapsedMilliseconds} ms: {vobs.InstanceCount} instances of " +
                    $"{vobs.MeshCount} meshes, {vobs.MissingCount} missing");
            }

            if (_args.Get("pfx", "true") == "true")
                BuildParticles(vfs, world, watch);

            var npcCount = _args.GetInt("npcs", 60);
            if (_args.Get("ai", "false") == "true")
            {
                // Real Old Camp NPCs with routines and AI states instead of the idle crowd.
                var humans = _humans = new HumanFactory(vfs, textures, materials[3], materials[4]);
                var ai = new GothicAi { Name = "GothicAi", BoneMode = _args.Get("bone-mode", "split"),
                    AnimationFar = _args.GetFloat("anim-far", 0f) };
                AddChild(ai);
                _ai = ai;
                ai.Init(vfs, humans, world, groundLight, _args.GetInt("hour", 12), GetCameraStart(world),
                    _args.GetFloat("ai-radius", 150f),
                    _args.Get("startup", _args.IsGothic2 ? "STARTUP_NEWWORLD" : "STARTUP_OLDCAMP"), _args.DataPath);
                Log($"AI NPCs spawned in {watch.ElapsedMilliseconds} ms: {ai.Count}");
            }
            else if (npcCount > 0)
            {
                var humans = _humans = new HumanFactory(vfs, textures, materials[3], materials[4]);
                var crowd = new HumanCrowd { Name = "Humans" };
                AddChild(crowd);
                _crowd = crowd;
                crowd.Spawn(humans, world, npcCount, groundLight, _args.Get("bone-mode", "split"), _args.IsGothic2);
                Log($"humans spawned in {watch.ElapsedMilliseconds} ms: {crowd.Count}");
            }

            if (_vobs?.ItemCount > 0)
                BuildItems(vfs, materials, watch);

            foreach (var skipped in _args.Get("skip", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse<WorldChunkBuilder.Kind>(skipped, true, out var kind))
                    builder.SkippedKinds.Add(kind);

            var isCollisionEnabled = _args.Get("collision", "true") == "true";
            builder.CreateNodes(worldRoot, kind => materials[(int)kind], isCollisionEnabled);
            if (isCollisionEnabled)
                Log($"collision: {builder.CollisionTriangleCount} triangles");
            if (builder.OccluderMinArea > 0)
                Log($"occluders: {builder.OccluderTriangleCount} triangles of at least {builder.OccluderMinArea} m²");
            Log($"world nodes created in {watch.ElapsedMilliseconds} ms: {builder.ChunkCount} chunks, " +
                $"{builder.TriangleCount} triangles, {textures.LayerCount} texture layers, {textures.MissingCount} missing");

            if (textures.MissingCount > 0)
                Log("textures missing: " + string.Join(", ", textures.MissingNames.Take(40)));
            // The hero's hands: his body mesh with his skin (before the texture array is built, a skin variant may add
            // a layer).
            if (_args.Get("player", "false") == "true")
            {
                _humans ??= new HumanFactory(vfs, textures, materials[3], materials[4]);
                var hero = _ai?.Hero;
                _handMesh = _humans.GetSkinnedMesh($"{(hero?.Body ?? "HUM_BODY_NAKED0").ToUpperInvariant()}.MDM",
                    hero?.BodyTexture ?? 0, hero?.SkinColor ?? 0) ?? _humans.GetSkinnedMesh("HUM_BODY_NAKED0.MDM");
            }

            var array = textures.Build();
            foreach (var material in materials)
            {
                material.SetShaderParameter("albedo_array", array);
                material.SetShaderParameter("light_scale", _args.GetFloat("light", 1f));
            }
            Log($"texture array {_args.SliceSize}px uploaded in {watch.ElapsedMilliseconds} ms");

            CreateEnvironment();
            if (_args.Get("player", "false") == "true")
                CreatePlayer(isXr, GetCameraStart(world));
            else
                CreateRig(isXr, GetCameraStart(world));

            // --record-audio=file.wav: what the master bus mixed, saved on quit (checks sound in headless runs).
            if (!string.IsNullOrEmpty(_args.Get("record-audio", "")))
            {
                _recorder = new AudioEffectRecord();
                AudioServer.AddBusEffect(0, _recorder);
                _recorder.SetRecordingActive(true);
            }

            if (_args.Get("music", "true") == "true")
            {
                var start = GetCameraStart(world);
                var music = new GothicGodot.Audio.GothicMusic { Name = "Music" };
                AddChild(music);
                music.Init(vfs, world, () => _camera != null ? _camera.GlobalPosition : start, _args.GetInt("hour", 12),
                    _args.GetFloat("music-volume", 0.33f));
                Log($"music in {watch.ElapsedMilliseconds} ms");
            }

            // Loading leaves the cached ZenKit world and the builders' data as garbage (over 1 GB). Collect and
            // compact it now: a big heap makes every later collection slower (frame spikes on the Quest).
            // Deferred: inside _Ready the locals (world, mesh) still keep it alive.
            if (_args.Get("collect", "true") == "true")
                Callable.From(CollectLoadingGarbage).CallDeferred();

            var shots = _args.Get("shots", "");
            if (!string.IsNullOrEmpty(shots))
                _shots = shots.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }

        public override void _Process(double delta)
        {
            _time += delta;
            if (_shots.Length > 0 && _time > 0.3)
            {
                ProcessShots(delta);
                return;
            }

            if (_rig != null)
                _rig.RotateY(Mathf.DegToRad(_turnDegreesPerSecond) * (float)delta);

            if (!_isScreenshotTaken && !string.IsNullOrEmpty(_args.Screenshot) && _time > 3)
            {
                _isScreenshotTaken = true;
                GetViewport().GetTexture().GetImage().SavePng(_args.Screenshot);
                Log($"screenshot {_args.Screenshot}");
            }

            if (_args.QuitAfterSeconds > 0 && _time > _args.QuitAfterSeconds)
                GetTree().Quit();
        }

        public override void _Notification(int what)
        {
            if (what != NotificationWMCloseRequest && what != NotificationPredelete || _recorder == null)
                return;
            var recording = _recorder.GetRecording();
            _recorder.SetRecordingActive(false);
            var path = _args.Get("record-audio", "");
            recording?.SaveToWav(path);
            Log($"audio recorded to {path}: {recording?.GetLength():F1} s");
            _recorder = null;
        }

        /// <summary>The world's items as grabbable bodies; their meshes come from the scripts (C_ITEM.visual).</summary>
        private void BuildItems(Vfs vfs, ShaderMaterial[] materials, Stopwatch watch)
        {
            var vm = _ai?.Vm ?? new ZenKit.DaedalusVm(vfs, "GOTHIC.DAT",
                (byte)(ZenKit.DaedalusExecutionFlags.AllowNullInstanceAccess |
                       ZenKit.DaedalusExecutionFlags.IgnoreConstSpecifier));
            if (_ai == null)
                vm.RegisterExternalDefault((_, _) => { });
            var items = new Dictionary<string, ZenKit.Daedalus.ItemInstance>(StringComparer.OrdinalIgnoreCase);

            ZenKit.Daedalus.ItemInstance ItemOf(string instance)
            {
                if (items.TryGetValue(instance, out var cached))
                    return cached;
                ZenKit.Daedalus.ItemInstance item = null;
                // Only real instances: InitInstance on another symbol ends the process natively.
                var symbol = vm.GetSymbolByName(instance);
                if (symbol is { Type: ZenKit.DaedalusDataType.Instance })
                    item = vm.InitInstance<ZenKit.Daedalus.ItemInstance>(symbol);
                items[instance] = item;
                return item;
            }

            var far = _args.GetFloat("item-far", 25f);
            ItemInfo = ItemOf;
            CreateItem = (instance, transform) =>
                _vobs.CreateItem(instance, ItemOf(instance)?.Visual, transform, materials[3], materials[4], far);

            var root = new Node3D { Name = "Items" };
            AddChild(root);
            _vobs.BuildItems(root, materials[3], materials[4], instance => ItemOf(instance)?.Visual, far);
            Log($"items built in {watch.ElapsedMilliseconds} ms: {_vobs.ItemsBuilt} of {_vobs.ItemCount} grabbable");
        }

        /// <summary>The script instance of an item by name (C_ITEM), cached.</summary>
        public Func<string, ZenKit.Daedalus.ItemInstance> ItemInfo { get; private set; }

        /// <summary>A new item body by instance name, e.g. taken out of the inventory (not yet in the tree).</summary>
        public Func<string, Transform3D, GothicGodot.Interaction.WorldItem> CreateItem { get; private set; }

        private void BuildParticles(Vfs vfs, ZenKit.World world, Stopwatch watch)
        {
            var root = new Node3D { Name = "Particles" };
            AddChild(root);
            var pfx = new PfxBuilder(vfs);

            void Walk(ZenKit.Vobs.IVirtualObject vob)
            {
                var visual = vob.Visual?.Name;
                if (vob.ShowVisual && visual != null && visual.EndsWith(".PFX", StringComparison.OrdinalIgnoreCase))
                    pfx.Add(visual, new Transform3D(VobBuilder.ToGodot(vob.Rotation), MeshPart.ToGodot(vob.Position)),
                        root);
                foreach (var child in vob.Children)
                    Walk(child);
            }

            foreach (var vob in world.RootObjects)
                Walk(vob);

            var far = _args.GetFloat("pfx-far", 0f);
            if (far > 0)
                AddChild(new PfxCuller(pfx.Emitters, far));
            Log($"particles built in {watch.ElapsedMilliseconds} ms: {pfx.EmitterCount} emitters, {pfx.MissingCount} missing");
        }

        /// <summary>
        /// Shots "x,y,z>tx,ty,tz" separated by ";": camera at x,y,z looking at tx,ty,tz, one screenshot each
        /// (file name from --screenshot with the shot number).
        /// </summary>
        private void ProcessShots(double delta)
        {
            _shotTime += delta;
            if (_shotIndex >= 0 && _shotTime < 1.5)
                return;

            if (_shotIndex >= 0)
            {
                var path = _args.Screenshot.Replace(".png", $"_{_shotIndex + 1}.png");
                GetViewport().GetTexture().GetImage().SavePng(path);
                Log($"shot {_shotIndex + 1}: {_shots[_shotIndex]} -> {path}");
            }

            _shotIndex++;
            _shotTime = 0;
            if (_shotIndex >= _shots.Length)
            {
                GetTree().Quit();
                return;
            }

            _isShotPending = true;
        }

        public override void _PhysicsProcess(double delta)
        {
            if (!_isShotPending)
                return;
            _isShotPending = false;

            Vector3 position;
            Vector3 target;
            var shot = _shots[_shotIndex];
            if (shot.StartsWith('#') && _crowd?.GetNpcTransform(shot[1..].Split(',')[0]) is { } npc)
            {
                // "#WAYPOINT[,distance]": in front of the human standing there, at eye height, looking at its face.
                var parts = shot[1..].Split(',');
                var distance = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 2.5f;
                var forward = -npc.Basis.Z.Normalized();
                target = npc.Origin + Vector3.Up * 1.5f;
                position = npc.Origin + forward * distance + Vector3.Up * 1.65f + npc.Basis.X.Normalized() * (distance * 0.35f);
            }
            else if (shot.StartsWith('@'))
            {
                // "@x,y,z" or "@x,y,z,distance": find a free spot with ground and a clear view of the target.
                var values = shot[1..].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                target = new Vector3(values[0], values[1], values[2]);
                position = FindViewpoint(target, values.Length > 3 ? values[3] : 5f) ?? target + new Vector3(4, 2, 4);
            }
            else
            {
                var parts = shot.Split('>');
                position = ParseVector(parts[0]);
                target = ParseVector(parts[1]);
            }

            _rig.GlobalPosition = position;
            _rig.Rotation = Vector3.Zero;
            _camera.Position = Vector3.Zero;
            _camera.LookAt(target);
            Log($"shot {_shotIndex + 1} camera {position} -> {target}");
        }

        private Vector3? FindViewpoint(Vector3 target, float preferredDistance)
        {
            var space = GetWorld3D().DirectSpaceState;
            Vector3? best = null;
            var bestScore = float.MaxValue;

            foreach (var distance in new[] { preferredDistance, preferredDistance * 1.5f, preferredDistance * 0.7f, preferredDistance * 2.2f })
            for (var step = 0; step < 24; step++)
            {
                var angle = Mathf.Tau * step / 24f;
                var probe = target + new Vector3(Mathf.Cos(angle) * distance, 0, Mathf.Sin(angle) * distance);
                var down = Ray(space, probe + Vector3.Up * 6f, probe + Vector3.Down * 12f);
                if (down == null)
                    continue;

                var eye = down.Value + Vector3.Up * 1.7f;
                if (eye.Y < target.Y - 3f || eye.Y > target.Y + 6f)
                    continue;

                // Not inside or right next to geometry.
                var isClear = true;
                foreach (var direction in new[] { Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back, Vector3.Up })
                {
                    if (Ray(space, eye, eye + direction * 0.5f) != null)
                    {
                        isClear = false;
                        break;
                    }
                }
                if (!isClear)
                    continue;

                var view = Ray(space, eye, target);
                if (view != null && view.Value.DistanceTo(target) > 1.0f)
                    continue;

                // Prefer the wanted distance and a little height above the target.
                var score = Mathf.Abs(eye.DistanceTo(target) - preferredDistance) + Mathf.Abs(eye.Y - target.Y - 1f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = eye;
                }
            }

            return best;
        }

        private static Vector3? Ray(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
        {
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
            return hit.Count > 0 ? (Vector3)hit["position"] : null;
        }

        private static Vector3 ParseVector(string text)
        {
            var values = text.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            return new Vector3(values[0], values[1], values[2]);
        }

        private void CreateEnvironment()
        {
            var dusk = _args.Get("dusk", "false") == "true";
            var sky = dusk ? new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.08f, 0.1f, 0.2f),
                SkyHorizonColor = new Color(0.45f, 0.3f, 0.25f),
                GroundHorizonColor = new Color(0.2f, 0.17f, 0.15f),
                GroundBottomColor = new Color(0.05f, 0.05f, 0.05f)
            } : new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.32f, 0.45f, 0.62f),
                SkyHorizonColor = new Color(0.68f, 0.72f, 0.74f),
                GroundHorizonColor = new Color(0.55f, 0.55f, 0.52f),
                GroundBottomColor = new Color(0.25f, 0.24f, 0.22f)
            };
            var environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                FogEnabled = true,
                FogLightColor = dusk ? new Color(0.25f, 0.2f, 0.2f) : new Color(0.62f, 0.66f, 0.7f),
                FogDensity = _args.GetFloat("fog", 0.0005f),
                FogSkyAffect = 0.3f,
                TonemapMode = Godot.Environment.ToneMapper.Linear
            };
            AddChild(new WorldEnvironment { Environment = environment });
        }

        private static void CollectLoadingGarbage()
        {
            var before = GC.GetTotalMemory(false);
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Log($"after loading: managed heap {before / 1048576} MB -> {GC.GetTotalMemory(true) / 1048576} MB");
        }

        /// <summary>--msaa=0|2|4|8 on every platform, --novsync for desktop measurements.</summary>
        private void ApplyRenderOptions()
        {
            var msaa = _args.GetInt("msaa", -1);
            if (msaa >= 0)
                GetViewport().Msaa3D = msaa switch
                {
                    0 => Viewport.Msaa.Disabled,
                    2 => Viewport.Msaa.Msaa2X,
                    8 => Viewport.Msaa.Msaa8X,
                    _ => Viewport.Msaa.Msaa4X
                };
            Log($"MSAA {GetViewport().Msaa3D}");
            if (_args.Get("novsync", "false") == "true")
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        }

        private bool TryStartXr()
        {
            var xr = XRServer.FindInterface("OpenXR");
            if (xr == null || !xr.Initialize())
                return false;

            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            GetViewport().UseXR = true;

            // Benchmark knobs: --msaa=0|2|4|8, --foveation=0..3. The refresh rate comes from the runtime
            // (tools/quest-bench.sh sets debug.oculus.refreshRate; requesting it here before the session runs made
            // the Quest fall back to 120 Hz).
            if (xr is OpenXRInterface openXr)
            {
                var foveation = _args.GetInt("foveation", -1);
                if (foveation >= 0)
                    openXr.FoveationLevel = foveation;
                Log($"OpenXR started: foveation {openXr.FoveationLevel}, " +
                    $"rates {string.Join("/", openXr.GetAvailableDisplayRefreshRates())}");
            }


            return true;
        }

        private ShaderMaterial[] CreateMaterials()
        {
            return new[] { "world_opaque", "world_alpha", "world_water", "vob_opaque", "vob_alpha" }
                .Select(name => new ShaderMaterial { Shader = GD.Load<Shader>($"res://Shaders/{name}.gdshader") })
                .ToArray();
        }

        private Vector3 GetCameraStart(ZenKit.World world)
        {
            var waypoint = _args.Get("waypoint", "");
            if (!string.IsNullOrEmpty(waypoint))
            {
                var point = world.WayNet.Points.FirstOrDefault(p => p.Name.Equals(waypoint, StringComparison.OrdinalIgnoreCase));
                if (point != null)
                    return WorldChunkBuilder.ToGodot(point.Position);
            }

            return _args.IsGothic2 ? GetCenter(world, "NW_CITY_") : GetOldCampCenter(world);
        }

        private static Vector3 GetCenter(ZenKit.World world, string prefix)
        {
            var points = world.WayNet.Points
                .Where(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => MeshPart.ToGodot(p.Position))
                .ToList();
            return points.Count == 0 ? Vector3.Zero : points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / points.Count;
        }

        private static Vector3 GetOldCampCenter(ZenKit.World world)
        {
            var ring = world.WayNet.Points
                .Where(p => p.Name.StartsWith("OC_ROUND_", StringComparison.OrdinalIgnoreCase))
                .Select(p => WorldChunkBuilder.ToGodot(p.Position))
                .ToList();
            if (ring.Count == 0)
                return Vector3.Zero;

            var center = ring.Aggregate(Vector3.Zero, (sum, p) => sum + p) / ring.Count;
            return center;
        }

        /// <summary>The playable player instead of the benchmark rig: walks on the world, the AI sees it as the hero.</summary>
        private void CreatePlayer(bool isXr, Vector3 position)
        {
            var player = new GothicGodot.Player.GothicPlayer { Name = "Player" };
            AddChild(player);
            player.GlobalPosition = position;
            player.Setup(isXr, _args.IsGothic2 ? 50f : 45f, _args.GetFloat("far", isXr ? 600f : 1500f),
                _args.GetFloat("walk-test", 0f), _args.GetFloat("teleport-test", 0f));
            player.Rotation = new Vector3(0, Mathf.DegToRad(_args.GetFloat("yaw", 0f)), 0);
            _ai?.SetHero(player);
            _camera = player.Camera;
            if (_handMesh != null)
                player.SetHands(_humans, _handMesh, new Color(0.75f, 0.75f, 0.75f));
            // --hand-preview: both hands from the side in front of the camera, right one open, left one closed.
            if (_handMesh != null && _args.Get("hand-preview", "false") == "true")
            {
                foreach (var (isLeft, grip, x) in new[] { (false, 0f, -0.1f), (true, 1f, 0.1f) })
                {
                    var hand = new GothicGodot.Player.GothicHand
                    {
                        Position = new Vector3(x, 0, -0.4f), Rotation = new Vector3(0, Mathf.Pi * 0.5f, 0),
                        Grip = () => grip
                    };
                    player.Camera.AddChild(hand);
                    hand.Setup(_humans, _handMesh, isLeft, new Color(0.9f, 0.9f, 0.9f));
                }
            }
            GothicGodot.Interaction.InventoryPanel bag = null;
            var itemRoot = GetNodeOrNull<Node3D>("Items");
            if (itemRoot != null && CreateItem != null)
            {
                bag = new GothicGodot.Interaction.InventoryPanel { Name = "Bag" };
                AddChild(bag);
                bag.Init(player, _ai?.Hero?.Inventory ?? new GothicGodot.Interaction.Inventory(), ItemInfo, CreateItem,
                    itemRoot);
                bag.IsAllowed = () => _ai?.Dialog?.IsActive != true;
            }

            var grabTest = _args.Get("grab-test", "");
            if (grabTest == "true" || grabTest == "bag")
            {
                var test = new GothicGodot.Interaction.GrabTest { Name = "GrabTest" };
                AddChild(test);
                test.Init(player, itemRoot, _args.Screenshot, grabTest == "bag" ? bag : null);
            }

            if (_ai?.Dialog != null)
            {
                var dialog = new GothicGodot.Dialog.DialogPanel { Name = "Dialog" };
                AddChild(dialog);
                dialog.Init(_ai, player, _args.Get("dialog-test", ""), _args.Screenshot);
            }
        }

        private void CreateRig(bool isXr, Vector3 position)
        {
            if (isXr)
            {
                var origin = new XROrigin3D();
                origin.AddChild(new XRCamera3D { Far = _args.GetFloat("far", 600f) });
                _rig = origin;
            }
            else
            {
                _rig = new Node3D();
                _camera = new Camera3D { Position = new Vector3(0, 1.7f, 0), Far = _args.GetFloat("far", 1500f), Fov = _args.GetFloat("fov", 75f) };
                _rig.AddChild(_camera);
            }

            AddChild(_rig);
            _rig.GlobalPosition = position;
            Log($"camera at {position}");
        }

        private static void Log(string message) => GD.Print($"[Spike] {message}");
    }
}
