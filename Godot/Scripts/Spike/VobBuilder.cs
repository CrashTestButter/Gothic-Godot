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
    /// Builds the world's static objects with a mesh visual (.3DS, compiled to .MRM): one ArrayMesh per visual,
    /// shared by its instances, one MeshInstance3D per object. Positions and rotations are mirrored on z like the
    /// world (Gothic left-handed, Godot right-handed).
    /// </summary>
    public class VobBuilder
    {
        private readonly Vfs _vfs;
        private readonly TextureArrayBuilder _textures;
        private readonly GroundLightSampler _light;
        private readonly Material _opaque;
        private readonly Material _alpha;
        private readonly Dictionary<string, ArrayMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);

        public int InstanceCount { get; private set; }
        public int MeshCount => _meshes.Count;
        public int MissingCount { get; private set; }

        public VobBuilder(Vfs vfs, TextureArrayBuilder textures, GroundLightSampler light, Material opaque,
            Material alpha)
        {
            _vfs = vfs;
            _textures = textures;
            _light = light;
            _opaque = opaque;
            _alpha = alpha;
        }

        public void Build(IEnumerable<IVirtualObject> roots, Node3D parent)
        {
            foreach (var root in roots)
                Walk(root, parent);
        }

        public static Basis ToGodot(Matrix3x3 m) => new(
            new Vector3(m.M11, m.M21, -m.M31),
            new Vector3(m.M12, m.M22, -m.M32),
            new Vector3(-m.M13, -m.M23, m.M33));

        private void Walk(IVirtualObject vob, Node3D parent)
        {
            var visual = vob.Visual?.Name;
            if (vob.ShowVisual && !string.IsNullOrEmpty(visual) && visual.EndsWith(".3DS", StringComparison.OrdinalIgnoreCase))
            {
                var mesh = GetMesh(visual);
                if (mesh != null)
                {
                    var position = WorldChunkBuilder.ToGodot(vob.Position);
                    var instance = new MeshInstance3D
                    {
                        Mesh = mesh,
                        Transform = new Transform3D(ToGodot(vob.Rotation), position),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                    };
                    parent.AddChild(instance);
                    instance.SetInstanceShaderParameter("tint", _light.Sample(position, new Color(0.6f, 0.6f, 0.6f)));
                    InstanceCount++;
                }
            }

            foreach (var child in vob.Children)
                Walk(child, parent);
        }

        private ArrayMesh GetMesh(string visual)
        {
            if (_meshes.TryGetValue(visual, out var cached))
                return cached;

            ArrayMesh result = null;
            try
            {
                var name = System.IO.Path.ChangeExtension(visual, ".MRM");
                var mrm = new MultiResolutionMesh(_vfs, name).Cache();
                result = BuildMesh(mrm);
            }
            catch (Exception)
            {
                MissingCount++;
            }

            _meshes[visual] = result;
            return result;
        }

        private ArrayMesh BuildMesh(IMultiResolutionMesh mrm)
        {
            var mesh = new ArrayMesh();
            var positions = mrm.Positions;

            foreach (var subMesh in mrm.SubMeshes)
            {
                var layer = _textures.GetLayer(subMesh.Material.Texture, out var hasAlpha);
                var wedges = subMesh.Wedges;
                var vertices = new Vector3[wedges.Count];
                var normals = new Vector3[wedges.Count];
                var uvs = new Vector2[wedges.Count];
                var layers = new Vector2[wedges.Count];

                for (var i = 0; i < wedges.Count; i++)
                {
                    var wedge = wedges[i];
                    vertices[i] = WorldChunkBuilder.ToGodot(positions[wedge.Index]);
                    normals[i] = new Vector3(wedge.Normal.X, wedge.Normal.Y, -wedge.Normal.Z);
                    uvs[i] = new Vector2(wedge.Texture.X, wedge.Texture.Y);
                    layers[i] = new Vector2(layer, 0);
                }

                var triangles = subMesh.Triangles;
                var indices = new int[triangles.Count * 3];
                for (var i = 0; i < triangles.Count; i++)
                {
                    indices[i * 3] = triangles[i].Wedge0;
                    indices[i * 3 + 1] = triangles[i].Wedge2;
                    indices[i * 3 + 2] = triangles[i].Wedge1;
                }

                if (indices.Length == 0)
                    continue;

                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.TexUV] = uvs;
                arrays[(int)Mesh.ArrayType.TexUV2] = layers;
                arrays[(int)Mesh.ArrayType.Index] = indices;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, hasAlpha || mrm.AlphaTest ? _alpha : _opaque);
            }

            return mesh.GetSurfaceCount() > 0 ? mesh : null;
        }
    }
}
