using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using ZenKit.Util;
using ZenKit.Vobs;
using Material = Godot.Material;
using Mesh = Godot.Mesh;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Builds the world's static objects with a mesh visual (.3DS, compiled to .MRM). Two modes:
    /// - Nodes: one ArrayMesh per visual shared by its instances, one MeshInstance3D per object (light as an instance
    ///   shader parameter).
    /// - Merged: every object's triangles go into the world cell of its origin (light baked into the vertex colour),
    ///   so objects cost no extra draw calls.
    /// Positions and rotations are mirrored on z like the world (Gothic left-handed, Godot right-handed).
    /// </summary>
    public class VobBuilder
    {
        private static readonly Vector3 _sunDirection = new Vector3(0.4f, 0.8f, 0.3f).Normalized();

        private class Part
        {
            public WorldChunkBuilder.Kind Kind;
            public int Layer;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public int[] Indices;
        }

        private readonly Vfs _vfs;
        private readonly TextureArrayBuilder _textures;
        private readonly GroundLightSampler _light;
        private readonly Dictionary<string, List<Part>> _parts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArrayMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);

        public int InstanceCount { get; private set; }
        public int MeshCount => _parts.Count;
        public int MissingCount { get; private set; }

        public VobBuilder(Vfs vfs, TextureArrayBuilder textures, GroundLightSampler light)
        {
            _vfs = vfs;
            _textures = textures;
            _light = light;
        }

        public static Basis ToGodot(Matrix3x3 m) => new(
            new Vector3(m.M11, m.M21, -m.M31),
            new Vector3(m.M12, m.M22, -m.M32),
            new Vector3(-m.M13, -m.M23, m.M33));

        public void BuildNodes(IEnumerable<IVirtualObject> roots, Node3D parent, Material opaque, Material alpha)
        {
            ForEachMeshObject(roots, (vob, parts, transform) =>
            {
                var instance = new MeshInstance3D
                {
                    Mesh = GetArrayMesh(vob.Visual.Name, parts, opaque, alpha),
                    Transform = transform,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                parent.AddChild(instance);
                instance.SetInstanceShaderParameter("tint", SampleLight(transform.Origin));
            });
        }

        public void BuildMerged(IEnumerable<IVirtualObject> roots, WorldChunkBuilder chunks)
        {
            ForEachMeshObject(roots, (vob, parts, transform) =>
            {
                var tint = SampleLight(transform.Origin);
                foreach (var part in parts)
                {
                    var count = part.Positions.Length;
                    var positions = new Vector3[count];
                    var normals = new Vector3[count];
                    var colors = new Color[count];
                    for (var i = 0; i < count; i++)
                    {
                        positions[i] = transform * part.Positions[i];
                        normals[i] = (transform.Basis * part.Normals[i]).Normalized();
                        var shade = 0.8f + 0.35f * Mathf.Max(normals[i].Dot(_sunDirection), 0f);
                        colors[i] = new Color(tint.R * shade, tint.G * shade, tint.B * shade);
                    }

                    chunks.AddObject(transform.Origin, part.Kind, positions, normals, part.Uvs, part.Layer, colors,
                        part.Indices);
                }
            });
        }

        private Color SampleLight(Vector3 position) => _light.Sample(position, new Color(0.6f, 0.6f, 0.6f));

        private void ForEachMeshObject(IEnumerable<IVirtualObject> roots,
            Action<IVirtualObject, List<Part>, Transform3D> action)
        {
            void Walk(IVirtualObject vob)
            {
                var visual = vob.Visual?.Name;
                if (vob.ShowVisual && !string.IsNullOrEmpty(visual) &&
                    visual.EndsWith(".3DS", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = GetParts(visual);
                    if (parts != null)
                    {
                        action(vob, parts,
                            new Transform3D(ToGodot(vob.Rotation), WorldChunkBuilder.ToGodot(vob.Position)));
                        InstanceCount++;
                    }
                }

                foreach (var child in vob.Children)
                    Walk(child);
            }

            foreach (var root in roots)
                Walk(root);
        }

        private List<Part> GetParts(string visual)
        {
            if (_parts.TryGetValue(visual, out var cached))
                return cached;

            List<Part> result = null;
            try
            {
                var mrm = new MultiResolutionMesh(_vfs, System.IO.Path.ChangeExtension(visual, ".MRM")).Cache();
                result = BuildParts(mrm);
            }
            catch (Exception)
            {
                MissingCount++;
            }

            _parts[visual] = result;
            return result;
        }

        private List<Part> BuildParts(IMultiResolutionMesh mrm)
        {
            var parts = new List<Part>();
            var positions = mrm.Positions;

            foreach (var subMesh in mrm.SubMeshes)
            {
                var triangles = subMesh.Triangles;
                if (triangles.Count == 0)
                    continue;

                var layer = _textures.GetLayer(subMesh.Material.Texture, out var hasAlpha);
                var wedges = subMesh.Wedges;
                var part = new Part
                {
                    Kind = hasAlpha || mrm.AlphaTest ? WorldChunkBuilder.Kind.AlphaTest : WorldChunkBuilder.Kind.Opaque,
                    Layer = layer,
                    Positions = new Vector3[wedges.Count],
                    Normals = new Vector3[wedges.Count],
                    Uvs = new Vector2[wedges.Count],
                    Indices = new int[triangles.Count * 3]
                };

                for (var i = 0; i < wedges.Count; i++)
                {
                    var wedge = wedges[i];
                    part.Positions[i] = WorldChunkBuilder.ToGodot(positions[wedge.Index]);
                    part.Normals[i] = new Vector3(wedge.Normal.X, wedge.Normal.Y, -wedge.Normal.Z);
                    part.Uvs[i] = new Vector2(wedge.Texture.X, wedge.Texture.Y);
                }

                for (var i = 0; i < triangles.Count; i++)
                {
                    part.Indices[i * 3] = triangles[i].Wedge0;
                    part.Indices[i * 3 + 1] = triangles[i].Wedge2;
                    part.Indices[i * 3 + 2] = triangles[i].Wedge1;
                }

                parts.Add(part);
            }

            return parts.Count > 0 ? parts : null;
        }

        private ArrayMesh GetArrayMesh(string visual, List<Part> parts, Material opaque, Material alpha)
        {
            if (_meshes.TryGetValue(visual, out var cached))
                return cached;

            var mesh = new ArrayMesh();
            foreach (var part in parts)
            {
                var layers = new Vector2[part.Positions.Length];
                Array.Fill(layers, new Vector2(part.Layer, 0));

                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = part.Positions;
                arrays[(int)Mesh.ArrayType.Normal] = part.Normals;
                arrays[(int)Mesh.ArrayType.TexUV] = part.Uvs;
                arrays[(int)Mesh.ArrayType.TexUV2] = layers;
                arrays[(int)Mesh.ArrayType.Index] = part.Indices;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1,
                    part.Kind == WorldChunkBuilder.Kind.AlphaTest ? alpha : opaque);
            }

            _meshes[visual] = mesh;
            return mesh;
        }
    }
}
