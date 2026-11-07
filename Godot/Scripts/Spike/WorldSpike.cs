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
        private GameClock _clock;
        private ShaderMaterial[] _materials;
        private Godot.Environment _environment;
        private ProceduralSkyMaterial _sky;
        private GothicGodot.Audio.GothicMusic _music;
        private double _daylightTime;
        private float _lastDaylight = -1f;
        private string[] _shots = Array.Empty<string>();
        private int _shotIndex = -1;
        private double _shotTime;
        private bool _isShotPending;
        private HumanCrowd _crowd;
        private GothicAi _ai;
        private AudioEffectRecord _recorder;
        private VobBuilder _vobs;
        private ModelFactory _humans;
        private ArrayMesh _handMesh;

        // Where the camera starts (what is made at load is near it).
        private Vector3 _start;

        public override void _Ready()
        {
            _args = new SpikeArgs();
            // The time of day's outdoor light, one value for every shader (also the ones made after loading).
            RenderingServer.GlobalShaderParameterAdd("sky_light", RenderingServer.GlobalShaderParameterType.Float, 1.0f);
            NativeLibraries.Register();
            AddChild(new GothicGodot.Port.GothicCoreHost { Name = "GothicCore" });
            AddChild(new PerfLog());

            var isXr = TryStartXr();
            ApplyRenderOptions();
            var watch = Stopwatch.StartNew();

            var vfs = new Vfs();
            // The world built on an earlier start: chunks, collision, occluders, ground light and texture layers. Read
            // on worker threads while the archives are mounted and the rest is set up.
            var cache = new WorldCache(_args);
            var textures = new TextureArrayBuilder(vfs, _args.SliceSize);
            var groundLight = new GroundLightSampler();
            System.Threading.Tasks.Task cacheRead = null;
            if (cache.IsHit)
            {
                cache.BeginLoadScene();
                cacheRead = System.Threading.Tasks.Task.WhenAll(
                    System.Threading.Tasks.Task.Run(() => textures.LoadCache(cache.TexturesPath)),
                    System.Threading.Tasks.Task.Run(() => cache.LoadLight(groundLight)));
            }
            else if (cache.IsEnabled)
                cache.Prepare();
            if (cache.IsEnabled)
                textures.CachePath = cache.TexturesPath;

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

            // A cached start takes the world's waynet and objects from the cache: the world file isn't read at all.
            var data = cache.IsHit ? cache.LoadData() : null;
            ZenKit.World world = null;
            ZenKit.IMesh mesh = null;
            if (data == null)
            {
                world = new ZenKit.World(vfs, _args.World, _args.IsGothic2 ? GameVersion.Gothic2 : GameVersion.Gothic1);
                // Not Cache(): that copies the whole mesh incl. its light maps into managed memory (G2: ~4.5 GB peak).
                mesh = world.Mesh;
                data = WorldData.Collect(world);
                if (cache.IsEnabled)
                    cache.SaveData(data);
                Log($"world {_args.World} loaded in {watch.ElapsedMilliseconds} ms: {mesh.PolygonCount} polygons, managed {GC.GetTotalMemory(false) / 1048576} MB");
            }
            else
            {
                Log($"world data from the cache in {watch.ElapsedMilliseconds} ms: {data.Waypoints.Count} waypoints, " +
                    $"{data.FreePoints.Count} freepoints, {data.Items.Count} items, {data.Mobs.Count} mobs, " +
                    $"{data.Effects.Count} effects, {data.Sounds.Count} sounds");
            }

            _start = GetCameraStart(data);

            var materials = _materials = CreateMaterials();
            // Gothic's time of day: --hour to start, --time-speed (1 = Gothic's 100 minute day, 0 = stands still).
            _clock = new GameClock { Name = "Clock", Speed = _args.GetFloat("time-speed", 1f) };
            AddChild(_clock);
            _clock.SetTime(_args.GetInt("hour", 12));
            var worldRoot = new Node3D { Name = "World" };
            AddChild(worldRoot);

            var builder = new WorldChunkBuilder(_args.CellSize)
            {
                ObjectFarDistance = _args.GetFloat("object-far", 0f),
                AlphaObjectFarDistance = _args.GetFloat("alpha-far", 0f),
                InteriorFarDistance = _args.GetFloat("interior-far", 40f),
                CompressAttributes = _args.Get("compress-meshes", "false") == "true",
                OccluderMinArea = _args.GetFloat("occluder-area", 0.5f)
            };
            if (builder.OccluderMinArea > 0)
                GetViewport().UseOcclusionCulling = true;
            if (cache.IsHit)
            {
                var waitStarted = watch.ElapsedMilliseconds;
                cacheRead.Wait();
                Log($"world cache {cache.Folder}: {textures.LayerCount} texture layers read in {textures.CacheReadMs:F0} ms " +
                    $"and the ground light beside the world file, waited {watch.ElapsedMilliseconds - waitStarted} ms for them");
            }
            else
            {
                var bsp = world.BspTree.Cache();
                builder.AddWorld(mesh, WorldChunkBuilder.GetLeafPolygons(bsp), textures, groundLight,
                    builder.InteriorFarDistance > 0 ? WorldChunkBuilder.GetSectorPolygons(bsp) : null);
                // The cached ZenKit mesh is the biggest managed object graph (G2: several hundred MB); free it now.
                mesh = null;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                if (cache.IsEnabled)
                    cache.SaveLight(groundLight);
                Log($"outdoor polygons {builder.OutdoorPolygons}, indoor {builder.IndoorPolygons}");
                Log($"world polygons collected in {watch.ElapsedMilliseconds} ms, managed {GC.GetTotalMemory(false) / 1048576} MB");
            }

            // --vob-mode=nodes (one node per object), merged (objects in the world cells) or none.
            var vobMode = _args.IsVobsEnabled ? _args.Get("vob-mode", "merged") : "none";
            if (vobMode != "none")
            {
                var vobs = new VobBuilder(vfs, textures, groundLight)
                {
                    SeparateItems = _args.Get("items", "true") == "true",
                    SeparateMobs = _args.Get("mobs", "true") == "true"
                };
                if (vobMode == "nodes")
                {
                    var vobRoot = new Node3D { Name = "Vobs" };
                    AddChild(vobRoot);
                    vobs.BuildNodes(world.RootObjects, vobRoot, materials[3], materials[4]);
                }
                else if (!cache.IsHit)
                {
                    vobs.BuildMerged(world.RootObjects, builder);
                }

                vobs.UseSeparated(data);

                _vobs = vobs;

                if (cache.IsHit)
                    Log($"objects in the cache, items and mobs collected in {watch.ElapsedMilliseconds} ms: " +
                        $"{vobs.ItemCount} items, {vobs.Mobs.Count} mobs");
                else
                    Log($"objects ({vobMode}) built in {watch.ElapsedMilliseconds} ms: {vobs.InstanceCount} instances of " +
                        $"{vobs.MeshCount} meshes, {vobs.MissingCount} missing");
            }

            if (_args.Get("pfx", "true") == "true")
                BuildParticles(vfs, data, watch);

            // --npc-draws=split: each sub-mesh and the head drawn on their own (the merged way's comparison).
            ModelFactory.IsMergingSurfaces = _args.Get("npc-draws", "merged") != "split";
            var npcCount = _args.GetInt("npcs", 60);
            if (_args.Get("ai", "false") == "true")
            {
                // Real Old Camp NPCs with routines and AI states instead of the idle crowd.
                var humans = _humans = new ModelFactory(vfs, textures, materials[3], materials[4]);
                var ai = new GothicAi { Name = "GothicAi", BoneMode = _args.Get("bone-mode", "split"),
                    AnimationFar = _args.GetFloat("anim-far", 0f) };
                AddChild(ai);
                _ai = ai;
                ai.Init(vfs, humans, data, groundLight, _args.GetInt("hour", 12), _start,
                    _args.GetFloat("ai-radius", 150f),
                    _args.Get("startup", _args.IsGothic2 ? "STARTUP_NEWWORLD" : "STARTUP_OLDCAMP"), _args.DataPath);
                Log($"AI NPCs spawned in {watch.ElapsedMilliseconds} ms: {ai.Count}");
            }
            else if (npcCount > 0)
            {
                var humans = _humans = new ModelFactory(vfs, textures, materials[3], materials[4]);
                var crowd = new HumanCrowd { Name = "Humans" };
                AddChild(crowd);
                _crowd = crowd;
                crowd.Spawn(humans, data, npcCount, groundLight, _args.Get("bone-mode", "split"), _args.IsGothic2);
                Log($"humans spawned in {watch.ElapsedMilliseconds} ms: {crowd.Count}");
            }

            if (_vobs?.ItemCount > 0)
            {
                BuildItems(vfs, materials, watch);
                BuildChests(vfs, materials, watch);
            }

            foreach (var skipped in _args.Get("skip", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse<WorldChunkBuilder.Kind>(skipped, true, out var kind))
                    builder.SkippedKinds.Add(kind);

            var isCollisionEnabled = _args.Get("collision", "true") == "true";
            Godot.Material MaterialOf(WorldChunkBuilder.Kind kind) => materials[(int)kind];
            Dictionary<string, int> counts;
            Node3D chunks;
            var addStarted = 0L;
            if (cache.IsHit)
            {
                var waitStarted = watch.ElapsedMilliseconds;
                chunks = cache.LoadScene(MaterialOf);
                addStarted = watch.ElapsedMilliseconds;
                counts = cache.ReadInfo();
                Log($"world scene: waited {addStarted - waitStarted} ms for the worker thread");
            }
            else
            {
                // Outside the scene while it's built and saved: the collision far away is taken out before it enters.
                chunks = new Node3D { Name = "Chunks" };
                builder.CreateNodes(chunks, MaterialOf, isCollisionEnabled);
                counts = new Dictionary<string, int>
                {
                    ["chunks"] = builder.ChunkCount, ["triangles"] = builder.TriangleCount,
                    ["collision"] = builder.CollisionTriangleCount, ["occluders"] = builder.OccluderTriangleCount
                };
                if (cache.IsEnabled)
                {
                    var saveStarted = watch.ElapsedMilliseconds;
                    cache.SaveScene(chunks, MaterialOf);
                    Log($"world saved to the cache in {watch.ElapsedMilliseconds - saveStarted} ms");
                }

                addStarted = watch.ElapsedMilliseconds;
            }

            // --collision-near=<m>: only the collision this near the start goes in now, the rest a piece per frame
            // (0: all of it now).
            var collisionNear = _args.GetFloat("collision-near", 40f);
            if (collisionNear > 0 && chunks.GetNodeOrNull<StaticBody3D>("WorldCollision") is { } collisionBody)
            {
                var start = _start;
                var streamer = new CollisionStreamer(collisionBody, start, collisionNear,
                    () => _camera != null ? _camera.GlobalPosition : start) { Name = "CollisionStreamer" };
                AddChild(streamer);
                Log($"collision within {collisionNear} m now, {streamer.PendingCount} pieces " +
                    $"({streamer.PendingTriangles} triangles) after loading");
            }

            worldRoot.AddChild(chunks);
            Log($"world scene added in {watch.ElapsedMilliseconds - addStarted} ms");

            if (isCollisionEnabled)
                Log($"collision: {counts.GetValueOrDefault("collision")} triangles");
            if (builder.OccluderMinArea > 0)
                Log($"occluders: {counts.GetValueOrDefault("occluders")} triangles of at least {builder.OccluderMinArea} m²");
            if (!cache.IsHit && builder.InteriorFarDistance > 0)
                Log($"interiors: {builder.InteriorCount} rooms with meshes of their own, drawn up to " +
                    $"{builder.InteriorFarDistance} m from their bounds");
            Log($"world nodes {(cache.IsHit ? "loaded" : "created")} in {watch.ElapsedMilliseconds} ms: " +
                $"{counts.GetValueOrDefault("chunks")} chunks, {counts.GetValueOrDefault("triangles")} triangles, " +
                $"{textures.LayerCount} texture layers, {textures.MissingCount} missing");

            if (textures.MissingCount > 0)
                Log("textures missing: " + string.Join(", ", textures.MissingNames.Take(40)));
            // The hero's hands: his body mesh with his skin (before the texture array is built, a skin variant may add
            // a layer).
            if (_args.Get("player", "false") == "true")
            {
                _humans ??= new ModelFactory(vfs, textures, materials[3], materials[4]);
                var hero = _ai?.Hero;
                _handMesh = _humans.GetSkinnedMesh($"{(hero?.Body ?? "HUM_BODY_NAKED0").ToUpperInvariant()}.MDM",
                    hero?.BodyTexture ?? 0, hero?.SkinColor ?? 0) ?? _humans.GetSkinnedMesh("HUM_BODY_NAKED0.MDM");
            }

            textures.Compression = _args.TextureCompression;
            var array = textures.Build();
            foreach (var material in materials)
            {
                material.SetShaderParameter("albedo_array", array);
                material.SetShaderParameter("light_scale", _args.GetFloat("light", 1f));
            }
            Log($"texture array {_args.SliceSize}px uploaded in {watch.ElapsedMilliseconds} ms (decoding {textures.DecodeMs:F0} ms, " +
                $"building {textures.BuildMs:F0} ms, {textures.Compression})");
            if (cache.IsEnabled && !cache.IsHit)
                cache.Complete(counts);

            CreateEnvironment();
            if (_args.Get("player", "false") == "true")
                CreatePlayer(isXr, _start);
            else
                CreateRig(isXr, _start);
            var spareCheck = _args.Get("spare-check", "");
            if (spareCheck != "" && _camera != null)
                AddSpareCheck(textures, spareCheck);

            // --record-audio=file.wav: what the master bus mixed, saved on quit (checks sound in headless runs).
            if (!string.IsNullOrEmpty(_args.Get("record-audio", "")))
            {
                _recorder = new AudioEffectRecord();
                AudioServer.AddBusEffect(0, _recorder);
                _recorder.SetRecordingActive(true);
            }

            if (_args.Get("music", "true") == "true")
            {
                var start = _start;
                var music = _music = new GothicGodot.Audio.GothicMusic { Name = "Music" };
                AddChild(music);
                music.Init(vfs, data, () => _camera != null ? _camera.GlobalPosition : start, _args.GetInt("hour", 12),
                    _args.GetFloat("music-volume", 0.33f));
                Log($"music in {watch.ElapsedMilliseconds} ms");
            }

            if (_args.Get("sounds", "true") == "true")
            {
                var start = _start;
                var sfx = new GothicGodot.Audio.GothicSfx { Name = "Sfx" };
                AddChild(sfx);
                sfx.Init(vfs);
                if (_ai != null)
                    _ai.Sfx = sfx;
                var sounds = new GothicGodot.Audio.WorldSounds { Name = "WorldSounds" };
                AddChild(sounds);
                if (_args.Get("world-sounds", "true") == "true")
                    sounds.Init(sfx, data.Sounds, () => _camera != null ? _camera.GlobalPosition : start,
                        _args.GetInt("hour", 12));
                Log($"sounds in {watch.ElapsedMilliseconds} ms: {sfx.Count} effects, {sounds.Count} sound objects");
            }

            // Loading leaves the cached ZenKit world and the builders' data as garbage (over 1 GB). Collect and
            // compact it now: a big heap makes every later collection slower (frame spikes on the Quest).
            // Deferred: inside _Ready the locals (world, mesh) still keep it alive.
            if (_args.Get("collect", "true") == "true")
                Callable.From(CollectLoadingGarbage).CallDeferred();

            var shots = _args.Get("shots", "");
            if (!string.IsNullOrEmpty(shots))
                _shots = shots.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (_bench != null)
                _bench.LoadMs = watch.ElapsedMilliseconds;
        }

        private Bench _bench;

        private bool _areChestsOpened;

        /// <summary>
        /// --spare-check=TEXTURE.TGA: that texture asked for after loading, on a quad in front of the camera - checks
        /// the array's spare layers (filled in the array's format).
        /// </summary>
        private void AddSpareCheck(TextureArrayBuilder textures, string texture)
        {
            var layer = textures.GetLayer(texture, out _);
            Log($"spare check: {texture} on layer {layer} of {textures.BuiltLayerCount}");
            var corners = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0) };
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Godot.Mesh.ArrayType.Max);
            arrays[(int)Godot.Mesh.ArrayType.Vertex] = corners;
            arrays[(int)Godot.Mesh.ArrayType.Normal] = corners.Select(_ => Vector3.Back).ToArray();
            arrays[(int)Godot.Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), Vector2.Zero };
            arrays[(int)Godot.Mesh.ArrayType.TexUV2] = corners.Select(_ => new Vector2(layer, 0)).ToArray();
            // Both sides.
            arrays[(int)Godot.Mesh.ArrayType.Index] = new[] { 0, 3, 2, 0, 2, 1, 0, 2, 3, 0, 1, 2 };
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, _materials[3]);
            var quad = new MeshInstance3D { Name = "SpareCheck", Mesh = mesh };
            AddChild(quad);
            var view = _camera.GlobalTransform;
            quad.GlobalTransform = new Transform3D(view.Basis, view.Origin - view.Basis.Z * 1.5f);
            quad.SetInstanceShaderParameter("tint", Colors.White);
        }

        /// <summary>The clock's minute for the AI and the music; light and sky follow the daylight once a second.</summary>
        private void OnMinute(int minute)
        {
            _ai?.SetMinute(minute);
            _music?.SetHour(minute / 60);
        }

        private static readonly Color _daySkyTop = new(0.32f, 0.45f, 0.62f);
        private static readonly Color _daySkyHorizon = new(0.68f, 0.72f, 0.74f);
        private static readonly Color _dayFog = new(0.62f, 0.66f, 0.7f);
        private static readonly Color _nightSkyTop = new(0.02f, 0.03f, 0.07f);
        private static readonly Color _nightSkyHorizon = new(0.08f, 0.09f, 0.14f);
        private static readonly Color _nightFog = new(0.05f, 0.06f, 0.09f);

        private void UpdateDaylight(double delta)
        {
            _daylightTime += delta;
            if (_daylightTime < 1.0 || _sky == null)
                return;
            _daylightTime = 0;
            var day = _clock.Daylight;
            if (Mathf.Abs(day - _lastDaylight) < 0.005f)
                return;
            _lastDaylight = day;
            // Gothic's baked light stays; outdoors gets darker at night like the engine's sky light (a third at night).
            var skyLight = Mathf.Lerp(0.35f, 1f, day);
            RenderingServer.GlobalShaderParameterSet("sky_light", skyLight);
            _sky.SkyTopColor = _nightSkyTop.Lerp(_daySkyTop, day);
            _sky.SkyHorizonColor = _nightSkyHorizon.Lerp(_daySkyHorizon, day);
            _sky.GroundHorizonColor = _sky.SkyHorizonColor * 0.8f;
            _environment.FogLightColor = _nightFog.Lerp(_dayFog, day);
        }

        public override void _Process(double delta)
        {
            _time += delta;
            if (_args.Get("dusk", "false") != "true")
                UpdateDaylight(delta);
            // --open-chests: every unlocked chest opens after a second (for shots of the lids).
            if (!_areChestsOpened && _time > 1 && _args.Get("open-chests", "false") == "true")
            {
                _areChestsOpened = true;
                foreach (var chest in _chests.Where(c => !c.IsLocked))
                    chest.Toggle();
            }

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
            // --item-near=<m>: item bodies made once the camera is this near (default 10 m before they are drawn;
            // 0: all at load).
            var near = _args.GetFloat("item-near", far > 0 ? far + 10f : 0f);
            NearSpawner spawner = null;
            var start = _start;
            if (near > 0)
            {
                spawner = new NearSpawner("items", () => _camera != null ? _camera.GlobalPosition : start, near, 8)
                    { Name = "ItemSpawner" };
                AddChild(spawner);
            }
            CreateItem = (instance, transform) =>
                _vobs.CreateItem(instance, ItemOf(instance)?.Visual, transform, materials[3], materials[4], far);

            var root = new Node3D { Name = "Items" };
            AddChild(root);
            _vobs.BuildItems(root, materials[3], materials[4], instance => ItemOf(instance)?.Visual, far, spawner, start);
            // The scripts' own items (G2's startup puts plants on freepoints), now and later.
            var inserted = 0;
            if (_ai != null)
            {
                foreach (var (instance, position) in _ai.InsertedItems)
                    if (CreateItem(instance, new Transform3D(Basis.Identity, position)) is { } item)
                    {
                        root.AddChild(item);
                        inserted++;
                    }

                _ai.ItemInserted += (instance, position) =>
                {
                    if (CreateItem(instance, new Transform3D(Basis.Identity, position)) is { } item)
                        root.AddChild(item);
                };
            }

            if (inserted > 0)
                Log($"items from the scripts: {inserted} of {_ai.InsertedItems.Count}");
            Log($"items built in {watch.ElapsedMilliseconds} ms: {_vobs.ItemsBuilt} of {_vobs.ItemCount} grabbable" +
                (spawner != null ? $" now ({spawner})" : ""));
        }

        private readonly List<GothicGodot.Interaction.WorldMob> _chests = new();

        /// <summary>
        /// The chests as animated models with their contents. Every item that can show up later (chest contents, what
        /// the hero and the NPCs carry) is loaded now: the texture array is built once, after loading.
        /// </summary>
        private void BuildChests(Vfs vfs, ShaderMaterial[] materials, Stopwatch watch)
        {
            // --preload-items=false: items carried or in chests get their textures when they show up (checks the
            // array's spare layers).
            var isPreloading = _args.Get("preload-items", "true") == "true";
            var factories = new Dictionary<string, ModelFactory>(StringComparer.OrdinalIgnoreCase);
            var root = new Node3D { Name = "Chests" };
            AddChild(root);
            var itemRoot = GetNode<Node3D>("Items");
            // Made when the camera comes within 10 m of their 50 m draw distance (--mob-near=0: all now); their
            // meshes and textures load now.
            var near = _args.GetFloat("mob-near", 60f);
            var start = _start;
            NearSpawner spawner = null;
            if (near > 0)
            {
                spawner = new NearSpawner("chests and doors", () => _camera != null ? _camera.GlobalPosition : start,
                    near, 4) { Name = "MobSpawner" };
                AddChild(spawner);
            }

            var chestCount = 0;
            var doorCount = 0;
            var lockedCount = 0;
            foreach (var mob in _vobs.Mobs)
            {
                var (model, transform) = (mob.Model, mob.Transform);
                if (!factories.TryGetValue(model, out var factory))
                {
                    try
                    {
                        factory = new ModelFactory(vfs, _vobs.Textures, materials[3], materials[4], model);
                    }
                    catch (Exception e)
                    {
                        GD.PushWarning($"[Chest] model {model} can't be loaded: {e.Message}");
                    }

                    factories[model] = factory;
                }

                if (factory == null ||
                    (factory.GetSkinnedMesh($"{factory.Model}.MDM") ?? factory.GetSkinnedMesh($"{factory.Model}.MDL")) == null)
                    continue;
                var (key, pick, contents) = (mob.Key, mob.PickString, mob.Contents ?? "");
                var isLocked = mob.IsLocked && (!string.IsNullOrEmpty(key) || !string.IsNullOrEmpty(pick));
                var isDoor = mob.IsDoor;
                var name = $"{(string.IsNullOrEmpty(mob.Name) ? model : mob.Name)}_{chestCount + doorCount}";
                var light = _vobs.LightAt(transform.Origin);
                void Create()
                {
                    var chest = new GothicGodot.Interaction.WorldMob { Name = name };
                    root.AddChild(chest);
                    chest.Transform = transform;
                    if (!chest.Setup(factory, contents, isLocked, light, CreateItem, itemRoot, isDoor, key, pick))
                    {
                        chest.QueueFree();
                        return;
                    }

                    _chests.Add(chest);
                }

                if (isDoor)
                    doorCount++;
                else
                    chestCount++;
                if (isLocked)
                    lockedCount++;
                if (spawner != null)
                    spawner.Add(transform.Origin, start, Create);
                else
                    Create();
                if (isPreloading)
                    foreach (var entry in Gothic.Core.Extensions.ContentItemExtension.UnpackItems(contents))
                        _vobs.Preload(ItemInfo(entry.Name.Trim())?.Visual);
            }

            // Every NPC's, also those out of range now.
            if (_ai != null && isPreloading)
                foreach (var npc in _ai.Inserted.Append(_ai.Hero))
                    foreach (var instance in npc.Inventory.Items)
                        _vobs.Preload(ItemInfo(instance)?.Visual);
            Log($"chests and doors built in {watch.ElapsedMilliseconds} ms: {chestCount} chests, {doorCount} doors of " +
                $"{_vobs.Mobs.Count} ({lockedCount} locked)" + (spawner != null ? $", {spawner}" : ""));
        }

        public IReadOnlyList<GothicGodot.Interaction.WorldMob> Chests => _chests;

        /// <summary>The script instance of an item by name (C_ITEM), cached.</summary>
        public Func<string, ZenKit.Daedalus.ItemInstance> ItemInfo { get; private set; }

        /// <summary>A new item body by instance name, e.g. taken out of the inventory (not yet in the tree).</summary>
        public Func<string, Transform3D, GothicGodot.Interaction.WorldItem> CreateItem { get; private set; }

        private void BuildParticles(Vfs vfs, WorldData data, Stopwatch watch)
        {
            var root = new Node3D { Name = "Particles" };
            AddChild(root);
            var pfx = new PfxBuilder(vfs);

            // With a particle distance (the Quest's 40 m) the emitters are made once the camera comes near.
            var far = _args.GetFloat("pfx-far", 0f);
            NearSpawner spawner = null;
            var start = _start;
            if (far > 0)
            {
                spawner = new NearSpawner("particle emitters", () => _camera != null ? _camera.GlobalPosition : start,
                    far + 10f, 4) { Name = "PfxSpawner" };
                AddChild(spawner);
            }

            foreach (var (visual, transform) in data.Effects)
            {
                if (spawner == null)
                    pfx.Add(visual, transform, root);
                else if (pfx.Prepare(visual))
                    spawner.Add(transform.Origin, start, () => pfx.Add(visual, transform, root));
            }

            if (far > 0)
                AddChild(new PfxCuller(pfx.Emitters, far));
            Log($"particles built in {watch.ElapsedMilliseconds} ms: {pfx.EmitterCount} emitters now, {pfx.MissingCount} missing" +
                (spawner != null ? $" ({spawner})" : ""));
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
            else if (shot.StartsWith('%') && _ai?.Npcs.FirstOrDefault(n =>
                         n.Factory?.Model == shot[1..].Split(',')[0].ToUpperInvariant() && n.Root != null) is { } scripted)
            {
                // "%MODEL[,distance]": a free view of the first scripted NPC of that model (WOLF, SHEEP, ...).
                var parts = shot[1..].Split(',');
                target = scripted.Position + Vector3.Up * 0.6f;
                position = FindViewpoint(target, parts.Length > 1
                    ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
                    : 3f) ?? target + new Vector3(2, 1, 2);
                Log($"shot of {scripted.Name} ({scripted.Factory.Model}) at {scripted.Position}");
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
            _environment = environment;
            _sky = sky;
            _clock.MinuteChanged += OnMinute;
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

        private Vector3 GetCameraStart(WorldData world)
        {
            var waypoint = _args.Get("waypoint", "");
            if (!string.IsNullOrEmpty(waypoint))
            {
                var point = world.Waypoints.FirstOrDefault(p => p.Name.Equals(waypoint, StringComparison.OrdinalIgnoreCase));
                if (point.Name != null)
                    return point.Position;
            }

            return _args.IsGothic2 ? GetCenter(world, "NW_CITY_") : GetOldCampCenter(world);
        }

        private static Vector3 GetCenter(WorldData world, string prefix)
        {
            var points = world.Waypoints
                .Where(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Position)
                .ToList();
            return points.Count == 0 ? Vector3.Zero : points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / points.Count;
        }

        private static Vector3 GetOldCampCenter(WorldData world)
        {
            var ring = world.Waypoints
                .Where(p => p.Name.StartsWith("OC_ROUND_", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Position)
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

            GothicGodot.Interaction.MouthUse mouth = null;
            if (_ai != null && bag != null)
            {
                mouth = new GothicGodot.Interaction.MouthUse { Name = "Mouth" };
                AddChild(mouth);
                mouth.Init(player, _ai.IsConsumable, _ai.UseItem, bag.GetName, () => _ai.HeroInstance);
            }

            var grabTest = _args.Get("grab-test", "");
            if (grabTest is "true" or "bag" or "use")
            {
                var test = new GothicGodot.Interaction.GrabTest { Name = "GrabTest" };
                AddChild(test);
                test.Init(player, itemRoot, _args.Screenshot, grabTest is "bag" or "use" ? bag : null,
                    grabTest == "use" ? mouth : null, () => _ai?.HeroInstance);
                if (_ai != null)
                    test.SetConsumable(_ai.IsConsumable);
            }

            GothicGodot.Dialog.DialogPanel dialogPanel = null;
            if (_ai?.Dialog != null)
            {
                var dialog = dialogPanel = new GothicGodot.Dialog.DialogPanel { Name = "Dialog" };
                AddChild(dialog);
                dialog.Init(_ai, player, _args.Get("dialog-test", ""), _args.Screenshot);
            }

            if (_ai?.Fight != null)
            {
                var swing = new GothicGodot.Interaction.MeleeSwing { Name = "MeleeSwing" };
                AddChild(swing);
                swing.Init(player, _ai, () => _ai.Dialog?.IsActive != true);
                if (_args.Get("fight-test", "false") != "false" && bag != null)
                {
                    var fightTest = new GothicGodot.Interaction.FightTest
                    {
                        Name = "FightTest", IsOneHit = _args.Get("fight-test", "") == "react"
                    };
                    AddChild(fightTest);
                    fightTest.Init(player, _ai, swing, bag, _args.Screenshot);
                }
            }

            if (_chests.Count > 0)
            {
                GothicGodot.Interaction.LockPicking locks = null;
                if (bag != null)
                {
                    locks = new GothicGodot.Interaction.LockPicking { Name = "LockPicking" };
                    AddChild(locks);
                    locks.Init(player, _ai, bag.Inventory, _args.IsGothic2);
                }

                var opener = new GothicGodot.Interaction.MobOpener { Name = "MobOpener" };
                AddChild(opener);
                opener.Init(player, _chests, () => _ai?.Dialog?.IsActive != true && dialogPanel?.HasTarget != true, locks);
                if (_args.Get("lock-test", "false") == "true")
                {
                    var lockTest = new GothicGodot.Interaction.LockTest { Name = "LockTest" };
                    AddChild(lockTest);
                    lockTest.Init(player, _chests, opener, locks, bag, _args.Screenshot);
                }
                // --bench=true: 12 waypoints within 250 m of the start (by name, evenly spread), four directions each.
                if (_args.Get("bench", "false") == "true" && _ai != null)
                {
                    var near = _ai.Waypoints.Where(w => w.Value.DistanceTo(_start) < 250f)
                        .OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase).Select(w => w.Value).ToList();
                    var count = Math.Min(12, near.Count);
                    _bench = new Bench { Name = "Bench" };
                    AddChild(_bench);
                    _bench.Init(player, Enumerable.Range(0, count).Select(i => near[i * near.Count / count]),
                        _args.Get("bench-shots", ""));
                }
                if (_args.Get("presence-test", "false") == "true" && _ai != null)
                {
                    var presenceTest = new PresenceTest { Name = "PresenceTest" };
                    AddChild(presenceTest);
                    presenceTest.Init(player, _ai, _args.Screenshot);
                }
                if (_args.Get("door-test", "false") == "true")
                {
                    var doorTest = new GothicGodot.Interaction.DoorTest { Name = "DoorTest" };
                    AddChild(doorTest);
                    doorTest.Init(player, _chests, _args.Screenshot);
                }

                if (_args.Get("chest-test", "false") == "true")
                {
                    var test = new GothicGodot.Interaction.ChestTest { Name = "ChestTest" };
                    AddChild(test);
                    test.Init(player, _chests, _args.Screenshot, GetNodeOrNull<GothicGodot.Interaction.InventoryPanel>("Bag"));
                }
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
