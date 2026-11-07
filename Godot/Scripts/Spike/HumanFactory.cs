using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using Material = Godot.Material;
using Mesh = Godot.Mesh;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Gothic humans for the spike: the HUMANS.MDH skeleton, skinned bodies or armours (.MDM soft skin, rest pose
    /// with the root at RootTranslation like Gothic-Unity's bind poses), heads (.MMB) and animations (.MAN) baked to
    /// per-frame bone poses. Bone index = hierarchy node index, the Skin binds every node.
    /// </summary>
    public class HumanFactory
    {
        public class Clip
        {
            public string Name;
            public int FrameCount;
            public float Fps;
            public int[] Nodes;
            public Vector3[] Positions;
            public Quaternion[] Rotations;
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

        public HumanFactory(Vfs vfs, TextureArrayBuilder textures, Material opaque, Material alpha)
        {
            _vfs = vfs;
            _textures = textures;
            _opaque = opaque;
            _alpha = alpha;

            var hierarchy = new ModelHierarchy(vfs, "HUMANS.MDH").Cache();
            var nodes = hierarchy.Nodes;
            BoneNames = new string[nodes.Count];
            Parents = new int[nodes.Count];
            Rests = new Transform3D[nodes.Count];
            var globals = new Transform3D[nodes.Count];
            Skin = new Skin();

            for (var i = 0; i < nodes.Count; i++)
            {
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

        public ArrayMesh GetSkinnedMesh(string mdmName)
        {
            if (_meshes.TryGetValue(mdmName, out var cached))
                return cached;

            ArrayMesh result = null;
            if (_vfs.Find(mdmName) != null)
            {
                var model = new ModelMesh(_vfs, mdmName).Cache();
                result = new ArrayMesh();
                foreach (var soft in model.Meshes)
                    AddSoftSkin(soft, result);
                if (result.GetSurfaceCount() == 0)
                    result = null;
            }

            _meshes[mdmName] = result;
            return result;
        }

        public ArrayMesh GetHeadMesh(string mmbName)
        {
            if (_meshes.TryGetValue(mmbName, out var cached))
                return cached;

            ArrayMesh result = null;
            if (_vfs.Find(mmbName) != null)
            {
                var morph = new MorphMesh(_vfs, mmbName).Cache();
                var parts = new List<MeshPart>();
                MeshPart.AddFromMrm(morph.Mesh, _textures, Transform3D.Identity, parts);
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

            _meshes[mmbName] = result;
            return result;
        }

        public Clip GetClip(string name)
        {
            if (_clips.TryGetValue(name, out var cached))
                return cached;

            Clip clip = null;
            var file = $"HUMANS-{name}.MAN";
            if (_vfs.Find(file) != null)
            {
                var animation = new ModelAnimation(_vfs, file).Cache();
                var nodes = animation.NodeIndices.ToArray();
                var samples = animation.Samples;
                var frames = Math.Min(animation.FrameCount, samples.Count / Math.Max(1, nodes.Length));
                clip = new Clip
                {
                    Name = name,
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

            _clips[name] = clip;
            return clip;
        }

        private void AddSoftSkin(ISoftSkinMesh soft, ArrayMesh mesh)
        {
            var mrm = soft.Mesh;
            var positions = mrm.Positions;
            var weights = soft.Weights;

            foreach (var subMesh in mrm.SubMeshes)
            {
                var triangles = subMesh.Triangles;
                if (triangles.Count == 0)
                    continue;

                var layer = _textures.GetLayer(subMesh.Material.Texture, out var hasAlpha);
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
