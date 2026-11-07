using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using Material = Godot.Material;
using Mesh = Godot.Mesh;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Builds the world mesh as one mesh per grid cell and kind (opaque, alpha tested, water), all sharing one texture
    /// array material - the same idea as Gothic-Unity's world chunks, without its BSP and light based slicing.
    ///
    /// Gothic is left-handed, Godot right-handed: z is mirrored, which also turns Gothic's counter-clockwise front
    /// faces into Godot's clockwise ones, so the fan keeps Gothic's corner order.
    /// </summary>
    public class WorldChunkBuilder
    {
        public enum Kind
        {
            Opaque,
            AlphaTest,
            Water
        }

        private class Chunk
        {
            public Kind Kind;
            public readonly List<Vector3> Positions = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Color> Colors = new();
            public readonly List<Vector2> Uvs = new();
            public readonly List<Vector2> Layers = new();
            public readonly List<int> Indices = new();
            public readonly Dictionary<(int Position, int Feature, int Material), int> Dedup = new();
        }

        private struct MaterialInfo
        {
            public int Layer;
            public Kind Kind;
        }

        public static bool IsWindingFlipped;

        public int ChunkCount { get; private set; }
        public int TriangleCount { get; private set; }

        public static Vector3 ToGodot(System.Numerics.Vector3 gothic) => new(gothic.X / 100f, gothic.Y / 100f, -gothic.Z / 100f);

        /// <summary>
        /// Gothic draws only the polygons its BSP leaves reference. The world mesh also holds about a quarter more:
        /// low detail copies that overlap the real geometry (G1: 32565 of 138215).
        /// </summary>
        public static List<int> GetLeafPolygons(IBspTree bspTree)
        {
            var polygonIndices = bspTree.PolygonIndices;
            var used = new HashSet<int>();
            var result = new List<int>();
            foreach (var leaf in bspTree.LeafNodeIndices)
            {
                var node = bspTree.GetNode(leaf);
                for (var i = node.PolygonIndex; i < node.PolygonIndex + node.PolygonCount; i++)
                {
                    if (used.Add(polygonIndices[i]))
                        result.Add(polygonIndices[i]);
                }
            }

            return result;
        }

        public void Build(IMesh mesh, IReadOnlyList<int> polygonIds, TextureArrayBuilder textures, float cellSize,
            Node3D parent, Func<Kind, Material> getMaterial, GroundLightSampler groundLight = null)
        {
            var materials = new MaterialInfo[mesh.MaterialCount];
            for (var i = 0; i < materials.Length; i++)
            {
                var material = mesh.GetMaterial(i);
                var layer = textures.GetLayer(material.Texture, out var hasAlpha);
                materials[i] = new MaterialInfo
                {
                    Layer = layer,
                    Kind = material.Group == MaterialGroup.Water ? Kind.Water : hasAlpha ? Kind.AlphaTest : Kind.Opaque
                };
            }

            var chunks = new Dictionary<(int X, int Z, Kind Kind), Chunk>();
            var polygons = mesh.Polygons;
            var positions = mesh.Positions;
            var features = mesh.Features;

            foreach (var polygonId in polygonIds)
            {
                var polygon = polygons[polygonId];
                if (polygon.IsPortal || polygon.IsGhostOccluder || polygon.PositionIndices.Count < 3)
                    continue;

                // Untextured polygons are invisible helpers in Gothic (occluders, collision walls).
                var info = materials[polygon.MaterialIndex];
                if (info.Layer == 0)
                    continue;
                var first = ToGodot(positions[polygon.PositionIndices[0]]);
                var key = ((int)Math.Floor(first.X / cellSize), (int)Math.Floor(first.Z / cellSize), info.Kind);
                if (!chunks.TryGetValue(key, out var chunk))
                {
                    chunk = new Chunk { Kind = info.Kind };
                    chunks[key] = chunk;
                }

                int AddCorner(int corner)
                {
                    var cornerKey = (polygon.PositionIndices[corner], polygon.FeatureIndices[corner], polygon.MaterialIndex);
                    if (chunk.Dedup.TryGetValue(cornerKey, out var index))
                        return index;

                    var feature = features[cornerKey.Item2];
                    index = chunk.Positions.Count;
                    chunk.Positions.Add(ToGodot(positions[cornerKey.Item1]));
                    chunk.Normals.Add(new Vector3(feature.Normal.X, feature.Normal.Y, -feature.Normal.Z));
                    var light = feature.Light;
                    chunk.Colors.Add(new Color(((light >> 16) & 0xFF) / 255f, ((light >> 8) & 0xFF) / 255f,
                        (light & 0xFF) / 255f, ((light >> 24) & 0xFF) / 255f));
                    chunk.Uvs.Add(new Vector2(feature.Texture.X, feature.Texture.Y));
                    chunk.Layers.Add(new Vector2(info.Layer, 0));
                    chunk.Dedup[cornerKey] = index;
                    if (info.Kind == Kind.Opaque)
                        groundLight?.Add(chunk.Positions[index], chunk.Colors[index]);
                    return index;
                }

                for (var p = 1; p < polygon.PositionIndices.Count - 1; p++)
                {
                    chunk.Indices.Add(AddCorner(0));
                    chunk.Indices.Add(AddCorner(IsWindingFlipped ? p + 1 : p));
                    chunk.Indices.Add(AddCorner(IsWindingFlipped ? p : p + 1));
                }
            }

            foreach (var ((x, z, kind), chunk) in chunks)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = chunk.Positions.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = chunk.Normals.ToArray();
                arrays[(int)Mesh.ArrayType.Color] = chunk.Colors.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = chunk.Uvs.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV2] = chunk.Layers.ToArray();
                arrays[(int)Mesh.ArrayType.Index] = chunk.Indices.ToArray();

                var arrayMesh = new ArrayMesh();
                arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                arrayMesh.SurfaceSetMaterial(0, getMaterial(kind));

                parent.AddChild(new MeshInstance3D
                {
                    Name = $"{kind}_{x}_{z}",
                    Mesh = arrayMesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                });

                ChunkCount++;
                TriangleCount += chunk.Indices.Count / 3;
            }
        }
    }
}
