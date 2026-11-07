using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using Material = Godot.Material;
using Mesh = Godot.Mesh;

namespace GothicGodot.Spike
{
    /// <summary>
    /// One of Gothic's animated models (HUMANS, WOLF, CHESTBIG_OCCHESTLARGE, ...): its skeleton (.MDH, or the .MDL's
    /// hierarchy), skinned bodies or armours (.MDM soft skin, rest pose with the root at RootTranslation like
    /// Gothic-Unity's bind poses), heads (.MMB) and animations ({MODEL}-{ANI}.MAN) baked to per-frame bone poses.
    /// Bone index = hierarchy node index, the Skin binds every node.
    /// </summary>
    public class ModelFactory
    {
        public class Clip
        {
            public string Name;
            public string Next;
            public int FrameCount;
            public float Fps;
            public int[] Nodes;
            public Vector3[] Positions;
            public Quaternion[] Rotations;

            public double Duration => Fps > 0 ? FrameCount / Fps : 0;

            /// <summary>
            /// Per channel: does the bone move within the clip? Gothic's clips carry a position for every bone, but
            /// mostly only the root's changes - writing the others each frame was most of the bone calls.
            /// </summary>
            public bool[] IsPositionAnimated;
            public bool[] IsRotationAnimated;

            internal void FindAnimatedChannels()
            {
                var channels = Nodes.Length;
                IsPositionAnimated = new bool[channels];
                IsRotationAnimated = new bool[channels];
                for (var frame = 1; frame < FrameCount; frame++)
                for (var channel = 0; channel < channels; channel++)
                {
                    var index = frame * channels + channel;
                    if (!IsPositionAnimated[channel] && Positions[index].DistanceSquaredTo(Positions[channel]) > 1e-8f)
                        IsPositionAnimated[channel] = true;
                    if (!IsRotationAnimated[channel] && Mathf.Abs(Rotations[index].Dot(Rotations[channel])) < 0.9999995f)
                        IsRotationAnimated[channel] = true;
                }
            }

            /// <summary>The pose at the time (seconds), frames interpolated; held on the last frame if not looping.</summary>
            /// <param name="isOnePoseCall">One SetBonePose per bone instead of position and rotation calls.</param>
            /// <param name="isFull">Every channel (the clip just started); else only the ones that move.</param>
            public void Apply(Skeleton3D skeleton, double time, bool isLooping, bool isOnePoseCall = false,
                bool isFull = true)
            {
                if (FrameCount == 0)
                    return;
                var t = time * Fps;
                var last = FrameCount - 1;
                if (isLooping)
                    t %= FrameCount;
                else if (t > last)
                    t = last;

                var frame0 = (int)t;
                var frame1 = isLooping ? (frame0 + 1) % FrameCount : Math.Min(frame0 + 1, last);
                var weight = (float)(t - frame0);
                var channels = Nodes.Length;
                for (var channel = 0; channel < channels; channel++)
                {
                    var isPositionSet = isFull || IsPositionAnimated == null || IsPositionAnimated[channel];
                    var isRotationSet = isFull || IsRotationAnimated == null || IsRotationAnimated[channel];
                    if (!isPositionSet && !isRotationSet)
                        continue;
                    var a = frame0 * channels + channel;
                    var b = frame1 * channels + channel;
                    var bone = Nodes[channel];
                    var position = Positions[a].Lerp(Positions[b], weight);
                    var rotation = Rotations[a].Slerp(Rotations[b], weight);
                    if (isOnePoseCall)
                    {
                        skeleton.SetBonePose(bone, new Transform3D(new Basis(rotation), position));
                        continue;
                    }

                    if (isPositionSet)
                        skeleton.SetBonePosePosition(bone, position);
                    if (isRotationSet)
                        skeleton.SetBonePoseRotation(bone, rotation);
                }
            }
        }

        private readonly Vfs _vfs;
        private readonly TextureArrayBuilder _textures;
        private readonly Material _opaque;
        private readonly Material _alpha;
        private readonly Dictionary<string, ArrayMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Clip> _clips = new(StringComparer.OrdinalIgnoreCase);

        public string[] BoneNames { get; }
        public int[] Parents { get; }
        public Transform3D[] Rests { get; }
        public Skin Skin { get; }
        public int RootNode { get; }

        // Hierarchy rest pose in Gothic space (row vectors, centimetres) for the weight offsets of soft skins.
        private readonly System.Numerics.Matrix4x4[] _gothicGlobals;

        public string Model { get; }
        private readonly Transform3D[] _restGlobals;
        public TextureArrayBuilder Textures => _textures;
        public Material Opaque => _opaque;
        public Material Alpha => _alpha;

        /// <param name="model">The model script's name without extension, e.g. HUMANS or WOLF.</param>
        public ModelFactory(Vfs vfs, TextureArrayBuilder textures, Material opaque, Material alpha,
            string model = "HUMANS")
        {
            Model = model.ToUpperInvariant();
            _vfs = vfs;
            _textures = textures;
            _opaque = opaque;
            _alpha = alpha;

            IModelHierarchy hierarchy = vfs.Find($"{Model}.MDH") != null
                ? new ModelHierarchy(vfs, $"{Model}.MDH").Cache()
                : new Model(vfs, $"{Model}.MDL").Cache().Hierarchy;
            var nodes = hierarchy.Nodes;
            BoneNames = new string[nodes.Count];
            Parents = new int[nodes.Count];
            Rests = new Transform3D[nodes.Count];
            var globals = _restGlobals = new Transform3D[nodes.Count];
            Skin = new Skin();
            _gothicGlobals = new System.Numerics.Matrix4x4[nodes.Count];

            for (var i = 0; i < nodes.Count; i++)
            {
                var gothicLocal = System.Numerics.Matrix4x4.Transpose(nodes[i].Transform);
                if (nodes[i].ParentIndex < 0)
                    gothicLocal.Translation = hierarchy.RootTranslation;
                _gothicGlobals[i] = nodes[i].ParentIndex < 0 ? gothicLocal : gothicLocal * _gothicGlobals[nodes[i].ParentIndex];

                BoneNames[i] = nodes[i].Name;
                Parents[i] = nodes[i].ParentIndex;
                var local = MeshPart.ToGodot(nodes[i].Transform);
                if (Parents[i] < 0)
                {
                    local.Origin = MeshPart.ToGodot(hierarchy.RootTranslation);
                    RootNode = i;
                }

                Rests[i] = local;
                globals[i] = Parents[i] < 0 ? local : globals[Parents[i]] * local;
                Skin.AddBind(i, globals[i].AffineInverse());
            }
        }

        public Skeleton3D CreateSkeleton()
        {
            var skeleton = new Skeleton3D();
            for (var i = 0; i < BoneNames.Length; i++)
            {
                skeleton.AddBone(BoneNames[i]);
                if (Parents[i] >= 0)
                    skeleton.SetBoneParent(i, Parents[i]);
                skeleton.SetBoneRest(i, Rests[i]);
                skeleton.SetBonePosePosition(i, Rests[i].Origin);
                skeleton.SetBonePoseRotation(i, Rests[i].Basis.GetRotationQuaternion());
            }

            return skeleton;
        }

        /// <summary>
        /// The engine's texture variants: "_V0_C0" in a body or head texture becomes "_V{variant}_C{skin}" from
        /// Mdl_SetVisualBody (body: body texture and skin colour, head: head texture and skin colour). Missing
        /// variants keep the original texture.
        /// </summary>
        private Func<string, string> GetVariantNames(int variant, int skin)
        {
            if (variant == 0 && skin == 0)
                return null;
            return name =>
            {
                var index = name.IndexOf("_V0_C0", StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    return name;
                var changed = $"{name[..index]}_V{variant}_C{skin}{name[(index + 6)..]}";
                return _textures.Exists(changed) ? changed : name;
            };
        }

        public ArrayMesh GetSkinnedMesh(string mdmName, int variant = 0, int skin = 0)
        {
            var key = $"{mdmName}|{variant}|{skin}";
            if (_meshes.TryGetValue(key, out var cached))
                return cached;

            ArrayMesh result = null;
            if (_vfs.Find(mdmName) != null)
            {
                // A model's .MDL holds its mesh too (meatbugs have no .MDM).
                var model = mdmName.EndsWith(".MDL", StringComparison.OrdinalIgnoreCase)
                    ? new ZenKit.Model(_vfs, mdmName).Cache().Mesh
                    : new ModelMesh(_vfs, mdmName).Cache();
                result = new ArrayMesh();
                var names = GetVariantNames(variant, skin);
                foreach (var soft in model.Meshes)
                    AddSoftSkin(soft, result, names);
                // Rigid parts hanging on one bone each (meatbugs, mobs' lids).
                foreach (var (node, mrm) in model.Attachments)
                    AddAttachment(node, mrm, result, names);
                if (result.GetSurfaceCount() == 0)
                    result = null;
            }

            _meshes[key] = result;
            return result;
        }

        public ArrayMesh GetHeadMesh(string mmbName, int variant = 0, int skin = 0)
        {
            var key = $"{mmbName}|{variant}|{skin}";
            if (_meshes.TryGetValue(key, out var cached))
                return cached;

            ArrayMesh result = null;
            if (_vfs.Find(mmbName) != null)
            {
                var morph = new MorphMesh(_vfs, mmbName).Cache();
                var parts = new List<MeshPart>();
                MeshPart.AddFromMrm(morph.Mesh, _textures, Transform3D.Identity, parts, GetVariantNames(variant, skin));
                result = new ArrayMesh();
                foreach (var part in parts)
                {
                    var layers = new Vector2[part.Positions.Length];
                    Array.Fill(layers, new Vector2(part.Layer, 0));
                    var arrays = NewArrays(part.Positions, part.Normals, part.Uvs, layers, part.Indices);
                    result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    result.SurfaceSetMaterial(result.GetSurfaceCount() - 1,
                        part.Kind == WorldChunkBuilder.Kind.AlphaTest ? _alpha : _opaque);
                }
            }

            _meshes[key] = result;
            return result;
        }

        public Clip GetClip(string name)
        {
            if (_clips.TryGetValue(name, out var cached))
                return cached;

            Clip clip = null;
            var file = $"{Model}-{name}.MAN";
            if (_vfs.Find(file) != null)
            {
                var animation = new ModelAnimation(_vfs, file).Cache();
                var nodes = animation.NodeIndices.ToArray();
                var samples = animation.Samples;
                var frames = Math.Min(animation.FrameCount, samples.Count / Math.Max(1, nodes.Length));
                clip = new Clip
                {
                    Name = name,
                    Next = string.IsNullOrEmpty(animation.Next) ? null : animation.Next.ToUpperInvariant(),
                    FrameCount = frames,
                    Fps = animation.Fps,
                    Nodes = nodes,
                    Positions = new Vector3[frames * nodes.Length],
                    Rotations = new Quaternion[frames * nodes.Length]
                };

                for (var frame = 0; frame < frames; frame++)
                for (var channel = 0; channel < nodes.Length; channel++)
                {
                    var index = frame * nodes.Length + channel;
                    var sample = samples[index];
                    var position = MeshPart.ToGodot(sample.Position);
                    // Root motion stays out: the root keeps only its height above the feet.
                    if (nodes[channel] == RootNode)
                        position = new Vector3(0, position.Y, 0);
                    clip.Positions[index] = position;
                    // Gothic stores the inverse rotation (Gothic-Unity negates w); mirroring z then flips x and y.
                    clip.Rotations[index] = new Quaternion(sample.Rotation.X, sample.Rotation.Y, -sample.Rotation.Z,
                        sample.Rotation.W).Normalized();
                }
            }

            clip?.FindAnimatedChannels();
            _clips[name] = clip;
            return clip;
        }

        private void AddAttachment(string nodeName, IMultiResolutionMesh mrm, ArrayMesh mesh,
            Func<string, string> textureName)
        {
            var node = Array.FindIndex(BoneNames, n => string.Equals(n, nodeName, StringComparison.OrdinalIgnoreCase));
            if (node < 0)
                return;
            var parts = new List<MeshPart>();
            MeshPart.AddFromMrm(mrm, _textures, _restGlobals[node], parts, textureName);
            foreach (var part in parts)
            {
                var count = part.Positions.Length;
                var layers = new Vector2[count];
                Array.Fill(layers, new Vector2(part.Layer, 0));
                var bones = new int[count * 4];
                var weights = new float[count * 4];
                for (var i = 0; i < count; i++)
                {
                    bones[i * 4] = node;
                    weights[i * 4] = 1f;
                }

                var arrays = NewArrays(part.Positions, part.Normals, part.Uvs, layers, part.Indices);
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1,
                    part.Kind == WorldChunkBuilder.Kind.AlphaTest ? _alpha : _opaque);
            }
        }

        private void AddSoftSkin(ISoftSkinMesh soft, ArrayMesh mesh, Func<string, string> textureName)
        {
            var mrm = soft.Mesh;
            var weights = soft.Weights;

            // Rest positions from the weights' bone offsets, like the engine: armours are modelled in another pose
            // than the skeleton (HUM_GRDM_ARMOR: 13-19 cm off), only the naked body's positions match it.
            var positions = new System.Numerics.Vector3[mrm.PositionCount];
            for (var p = 0; p < positions.Length; p++)
            {
                var sum = System.Numerics.Vector3.Zero;
                foreach (var entry in weights[p])
                {
                    if (entry.NodeIndex < _gothicGlobals.Length)
                        sum += entry.Weight * System.Numerics.Vector3.Transform(entry.Position, _gothicGlobals[entry.NodeIndex]);
                }
                positions[p] = sum;
            }

            foreach (var subMesh in mrm.SubMeshes)
            {
                var triangles = subMesh.Triangles;
                if (triangles.Count == 0)
                    continue;

                var texture = subMesh.Material.Texture;
                var layer = _textures.GetLayer(textureName != null ? textureName(texture) : texture, out var hasAlpha);
                var wedges = subMesh.Wedges;
                var count = wedges.Count;
                var vertices = new Vector3[count];
                var normals = new Vector3[count];
                var uvs = new Vector2[count];
                var layers = new Vector2[count];
                var bones = new int[count * 4];
                var boneWeights = new float[count * 4];

                for (var i = 0; i < count; i++)
                {
                    var wedge = wedges[i];
                    vertices[i] = MeshPart.ToGodot(positions[wedge.Index]);
                    normals[i] = new Vector3(wedge.Normal.X, wedge.Normal.Y, -wedge.Normal.Z);
                    uvs[i] = new Vector2(wedge.Texture.X, wedge.Texture.Y);
                    layers[i] = new Vector2(layer, 0);

                    var entries = weights[wedge.Index];
                    var total = 0f;
                    for (var k = 0; k < Math.Min(4, entries.Count); k++)
                        total += entries[k].Weight;
                    for (var k = 0; k < 4; k++)
                    {
                        var isUsed = k < entries.Count;
                        bones[i * 4 + k] = isUsed ? entries[k].NodeIndex : 0;
                        boneWeights[i * 4 + k] = isUsed && total > 0 ? entries[k].Weight / total : 0f;
                    }
                }

                var indices = new int[triangles.Count * 3];
                for (var i = 0; i < triangles.Count; i++)
                {
                    indices[i * 3] = triangles[i].Wedge0;
                    indices[i * 3 + 1] = triangles[i].Wedge2;
                    indices[i * 3 + 2] = triangles[i].Wedge1;
                }

                var arrays = NewArrays(vertices, normals, uvs, layers, indices);
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = boneWeights;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, hasAlpha ? _alpha : _opaque);
            }
        }

        private static Godot.Collections.Array NewArrays(Vector3[] vertices, Vector3[] normals, Vector2[] uvs,
            Vector2[] layers, int[] indices)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.TexUV2] = layers;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            return arrays;
        }
    }
}
