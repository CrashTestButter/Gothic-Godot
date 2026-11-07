using System;
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

        public override void _Ready()
        {
            _args = new SpikeArgs();
            NativeLibraries.Register();
            AddChild(new PerfLog());

            var isXr = TryStartXr();
            var watch = Stopwatch.StartNew();

            var vfs = new Vfs();
            var dataPath = Path.Combine(_args.DataPath, "Data");
            foreach (var file in Directory.GetFiles(dataPath)
                         .Where(f => f.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase)))
                vfs.MountDisk(file, VfsOverwriteBehavior.Older);
            Log($"mounted {dataPath} in {watch.ElapsedMilliseconds} ms");

            var world = new ZenKit.World(vfs, _args.World, GameVersion.Gothic1);
            var mesh = world.Mesh.Cache();
            Log($"world {_args.World} loaded in {watch.ElapsedMilliseconds} ms: {mesh.PolygonCount} polygons");

            var textures = new TextureArrayBuilder(vfs, _args.SliceSize);
            var materials = CreateMaterials();
            var worldRoot = new Node3D { Name = "World" };
            AddChild(worldRoot);

            var groundLight = new GroundLightSampler();
            var builder = new WorldChunkBuilder(_args.CellSize);
            builder.AddWorld(mesh, WorldChunkBuilder.GetLeafPolygons(world.BspTree.Cache()), textures, groundLight);
            Log($"world polygons collected in {watch.ElapsedMilliseconds} ms");

            // --vob-mode=nodes (one node per object), merged (objects in the world cells) or none.
            var vobMode = _args.IsVobsEnabled ? _args.Get("vob-mode", "merged") : "none";
            if (vobMode != "none")
            {
                var vobs = new VobBuilder(vfs, textures, groundLight);
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

                Log($"objects ({vobMode}) built in {watch.ElapsedMilliseconds} ms: {vobs.InstanceCount} instances of " +
                    $"{vobs.MeshCount} meshes, {vobs.MissingCount} missing");
            }

            builder.CreateNodes(worldRoot, kind => materials[(int)kind]);
            Log($"world nodes created in {watch.ElapsedMilliseconds} ms: {builder.ChunkCount} chunks, " +
                $"{builder.TriangleCount} triangles, {textures.LayerCount} texture layers, {textures.MissingCount} missing");

            var array = textures.Build();
            foreach (var material in materials)
                material.SetShaderParameter("albedo_array", array);
            Log($"texture array {_args.SliceSize}px uploaded in {watch.ElapsedMilliseconds} ms");

            CreateEnvironment();
            CreateRig(isXr, GetCameraStart(world));

            var shots = _args.Get("shots", "");
            if (!string.IsNullOrEmpty(shots))
                _shots = shots.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }

        public override void _Process(double delta)
        {
            _time += delta;
            if (_shots.Length > 0)
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

            var parts = _shots[_shotIndex].Split('>');
            _rig.GlobalPosition = ParseVector(parts[0]);
            _rig.Rotation = Vector3.Zero;
            _camera.Position = Vector3.Zero;
            _camera.LookAt(ParseVector(parts[1]));
        }

        private static Vector3 ParseVector(string text)
        {
            var values = text.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            return new Vector3(values[0], values[1], values[2]);
        }

        private void CreateEnvironment()
        {
            var sky = new ProceduralSkyMaterial
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
                FogLightColor = new Color(0.62f, 0.66f, 0.7f),
                FogDensity = _args.GetFloat("fog", 0.0005f),
                FogSkyAffect = 0.3f,
                TonemapMode = Godot.Environment.ToneMapper.Linear
            };
            AddChild(new WorldEnvironment { Environment = environment });
        }

        private bool TryStartXr()
        {
            var xr = XRServer.FindInterface("OpenXR");
            if (xr == null || !xr.Initialize())
                return false;

            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            GetViewport().UseXR = true;
            Log("OpenXR started");
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

            return GetOldCampCenter(world);
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

        private void CreateRig(bool isXr, Vector3 position)
        {
            if (isXr)
            {
                var origin = new XROrigin3D();
                origin.AddChild(new XRCamera3D { Far = 300 });
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
