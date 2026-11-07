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

        private readonly Vfs _vfs;
        private readonly TextureArrayBuilder _textures;
        private readonly GroundLightSampler _light;
        private readonly Dictionary<string, List<MeshPart>> _parts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ArrayMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Shape3D> _shapes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(IItem Vob, string Visual, Transform3D Transform)> _items = new();

        /// <summary>Items (oCItem) are left out of the merged cells and collected for <see cref="BuildItems"/>.</summary>
        public bool SeparateItems { get; set; }
        public int ItemCount => _items.Count;
        public int ItemsBuilt { get; private set; }

        public int InstanceCount { get; private set; }
        public int MeshCount => _parts.Count;
        public int MissingCount { get; private set; }

        public VobBuilder(Vfs vfs, TextureArrayBuilder textures, GroundLightSampler light)
        {
            _vfs = vfs;
            _textures = textures;
            _light = light;
        }

        public static Basis ToGodot(Matrix3x3 m) =>
            MeshPart.ToGodotBasis(m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33);

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
                    // Gothic lets only objects with cdStatic collide (fences, barrels, mobs - not grass or bushes).
                    if (vob.CdStatic && part.Kind == WorldChunkBuilder.Kind.Opaque)
                        chunks.AddObjectCollision(transform.Origin, positions, part.Indices);
                }
            });
        }

        /// <summary>
        /// The collected items as single bodies the hands can take: frozen where Gothic put them (no physics cost)
        /// until they are grabbed, then thrown and falling with physics.
        /// </summary>
        public void BuildItems(Node3D parent, Material opaque, Material alpha, Func<string, string> visualOf, float far)
        {
            foreach (var (vob, savedVisual, transform) in _items)
            {
                var item = CreateItem(vob.Instance, visualOf(vob.Instance) ?? savedVisual, transform, opaque, alpha, far);
                if (item == null)
                    continue;
                parent.AddChild(item);
                ItemsBuilt++;
            }
        }

        /// <summary>One item body (also for items taken out of the inventory); null without a mesh.</summary>
        public GothicGodot.Interaction.WorldItem CreateItem(string instance, string visual, Transform3D transform,
            Material opaque, Material alpha, float far)
        {
            if (string.IsNullOrEmpty(visual) || !IsMeshVisual(visual))
                return null;
            var parts = GetParts(visual);
            if (parts == null)
                return null;
            var mesh = GetArrayMesh(visual, parts, opaque, alpha);
            if (!_shapes.TryGetValue(visual, out var shape))
                _shapes[visual] = shape = mesh.CreateConvexShape(true, true);
            var item = new GothicGodot.Interaction.WorldItem { Name = instance, Instance = instance, Transform = transform };
            item.Setup(mesh, shape, SampleLight(transform.Origin), far);
            return item;
        }

        private Color SampleLight(Vector3 position) => _light.Sample(position, new Color(0.6f, 0.6f, 0.6f));

        private void ForEachMeshObject(IEnumerable<IVirtualObject> roots,
            Action<IVirtualObject, List<MeshPart>, Transform3D> action)
        {
            void Walk(IVirtualObject vob)
            {
                var visual = vob.Visual?.Name;
                if (SeparateItems && vob is IItem item)
                {
                    // The visual comes from the item's script instance (C_ITEM.visual), resolved in BuildItems.
                    _items.Add((item, visual,
                        new Transform3D(ToGodot(vob.Rotation), WorldChunkBuilder.ToGodot(vob.Position))));
                }
                else if (vob.ShowVisual && !string.IsNullOrEmpty(visual) && IsMeshVisual(visual))
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

        private static bool IsMeshVisual(string visual) =>
            visual.EndsWith(".3DS", StringComparison.OrdinalIgnoreCase) ||
            visual.EndsWith(".ASC", StringComparison.OrdinalIgnoreCase) ||
            visual.EndsWith(".MDS", StringComparison.OrdinalIgnoreCase);

        private List<MeshPart> GetParts(string visual)
        {
            if (_parts.TryGetValue(visual, out var cached))
                return cached;

            List<MeshPart> result = null;
            try
            {
                if (visual.EndsWith(".3DS", StringComparison.OrdinalIgnoreCase))
                {
                    var mrm = new MultiResolutionMesh(_vfs, System.IO.Path.ChangeExtension(visual, ".MRM")).Cache();
                    result = new List<MeshPart>();
                    MeshPart.AddFromMrm(mrm, _textures, Transform3D.Identity, result);
                    if (result.Count == 0)
                        result = null;
                }
                else
                {
                    result = StaticModelLoader.TryLoad(_vfs, visual, _textures);
                }
            }
            catch (Exception)
            {
                result = null;
            }

            if (result == null)
                MissingCount++;
            _parts[visual] = result;
            return result;
        }

        private ArrayMesh GetArrayMesh(string visual, List<MeshPart> parts, Material opaque, Material alpha)
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
