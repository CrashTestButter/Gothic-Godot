using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// The built world saved on the first start and loaded on the next ones (Gothic-Unity's pre-caching idea): the
    /// chunk meshes with their collision and occluders, the ground light and the texture array with its layer names.
    /// What stays dynamic (NPCs, items, chests, particles, sounds) is built every time.
    ///
    /// The key holds the game data (archive names, sizes, dates), the world, the build options, the Godot version and
    /// this build of the code: any change builds and saves it again, an old cache of the same world is deleted then.
    /// --world-cache=false builds without it, --world-cache=rebuild ignores a saved one.
    /// </summary>
    public class WorldCache
    {
        private const string _root = "user://world-cache";

        // Options that change what the world build produces.
        private static readonly string[] _keyOptions =
        {
            "cell", "slice", "vobs", "vob-mode", "items", "mobs", "object-far", "alpha-far", "compress-meshes",
            "occluder-area", "collision", "skip"
        };

        private readonly string _prefix;
        private readonly string _key;

        /// <summary>False with --world-cache=false and for --vob-mode=nodes (objects as nodes, not in the cells).</summary>
        public bool IsEnabled { get; }

        /// <summary>A complete cache for this key exists: the world is loaded from it.</summary>
        public bool IsHit { get; }

        public string Folder { get; }
        public string TexturesPath => Path.Combine(Folder, "textures.bin");
        private string ScenePath => Path.Combine(Folder, "world.scn");
        private string LightPath => Path.Combine(Folder, "light.bin");

        // Written last: a cache without it was cut off (crash, quit) and is built again.
        private string InfoPath => Path.Combine(Folder, "info.txt");

        public WorldCache(SpikeArgs args)
        {
            var mode = args.Get("world-cache", "true");
            IsEnabled = mode != "false" && args.Get("vob-mode", "merged") != "nodes";

            var key = new StringBuilder();
            key.AppendLine($"godot {Engine.GetVersionInfo()["string"]}");
            key.AppendLine($"code {typeof(WorldCache).Assembly.ManifestModule.ModuleVersionId}");
            key.AppendLine($"game {args.Game} world {args.World.ToUpperInvariant()}");
            var dataPath = Path.Combine(args.DataPath, "Data");
            if (Directory.Exists(dataPath))
                foreach (var file in Directory.GetFiles(dataPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(file);
                    key.AppendLine($"data {info.Name} {info.Length} {info.LastWriteTimeUtc.Ticks}");
                }

            foreach (var option in _keyOptions)
                key.AppendLine($"--{option}={args.Get(option, "")}");
            key.AppendLine($"textures {args.TextureCompression}");
            _key = key.ToString();

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key)))[..16].ToLowerInvariant();
            _prefix = $"g{args.Game}-{Path.GetFileNameWithoutExtension(args.World).ToLowerInvariant()}-";
            Folder = Path.Combine(ProjectSettings.GlobalizePath(_root), _prefix + hash);
            IsHit = IsEnabled && mode != "rebuild" && File.Exists(InfoPath) && File.Exists(ScenePath) &&
                    File.Exists(LightPath) && File.Exists(TexturesPath);
        }

        /// <summary>Before a build that gets saved: an empty folder, older caches of this world deleted.</summary>
        public void Prepare()
        {
            var root = ProjectSettings.GlobalizePath(_root);
            if (Directory.Exists(root))
                foreach (var folder in Directory.GetDirectories(root, _prefix + "*"))
                    Directory.Delete(folder, true);
            Directory.CreateDirectory(Folder);
        }

        /// <summary>The counts saved with the cache (for the load log).</summary>
        public Dictionary<string, int> ReadInfo()
        {
            var result = new Dictionary<string, int>();
            foreach (var line in File.ReadAllLines(InfoPath))
            {
                var split = line.IndexOf('=');
                if (split > 0 && int.TryParse(line[(split + 1)..], out var value))
                    result[line[..split]] = value;
            }

            return result;
        }

        /// <summary>Marks the cache complete; the key is kept in it to see what it was built from.</summary>
        public void Complete(Dictionary<string, int> counts)
        {
            var text = new StringBuilder(_key);
            foreach (var (name, value) in counts)
                text.AppendLine($"{name}={value}");
            File.WriteAllText(InfoPath, text.ToString());
        }

        /// <summary>
        /// Saves the chunk meshes, collision and occluders. Their materials are left out (they are the shared ones,
        /// set again on load by each mesh's kind).
        /// </summary>
        public void SaveScene(Node3D chunks, Func<WorldChunkBuilder.Kind, Material> getMaterial)
        {
            SetOwner(chunks, chunks);
            WorldChunkBuilder.SetMaterials(chunks, _ => null);
            var scene = new PackedScene();
            var packed = scene.Pack(chunks);
            var saved = packed == Error.Ok ? ResourceSaver.Save(scene, ScenePath) : packed;
            WorldChunkBuilder.SetMaterials(chunks, getMaterial);
            if (saved != Error.Ok)
                GD.PrintErr($"[Cache] saving the world failed: {saved}");
        }

        private bool _isSceneRequested;

        /// <summary>Starts reading the chunk scene on a worker thread: it loads while the NPCs and items are made.</summary>
        public void BeginLoadScene()
        {
            _isSceneRequested = ResourceLoader.LoadThreadedRequest(ScenePath, "", false,
                ResourceLoader.CacheMode.Ignore) == Error.Ok;
        }

        public Node3D LoadScene(Func<WorldChunkBuilder.Kind, Material> getMaterial)
        {
            var scene = _isSceneRequested
                ? ResourceLoader.LoadThreadedGet(ScenePath) as PackedScene
                : ResourceLoader.Load<PackedScene>(ScenePath, null, ResourceLoader.CacheMode.Ignore);
            if (scene == null)
            {
                // Unreadable (cut off, disk full): this start has no world, the next one builds it again.
                GD.PrintErr($"[Cache] {ScenePath} can't be read, the world is built again on the next start");
                File.Delete(InfoPath);
                return new Node3D { Name = "Chunks" };
            }

            var chunks = scene.Instantiate<Node3D>();
            WorldChunkBuilder.SetMaterials(chunks, getMaterial);
            return chunks;
        }

        public void SaveLight(GroundLightSampler light) => light.Save(LightPath);

        public void LoadLight(GroundLightSampler light) => light.Load(LightPath);

        private static void SetOwner(Node node, Node owner)
        {
            foreach (var child in node.GetChildren())
            {
                child.Owner = owner;
                SetOwner(child, owner);
            }
        }
    }
}
