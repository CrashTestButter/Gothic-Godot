using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ZenKit;
using Material = Godot.Material;
using Mesh = Godot.Mesh;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Builds the world mesh as one mesh per grid cell and kind (opaque, alpha tested, water), all sharing one texture
    /// array material - the same idea as Gothic-Unity's world chunks, without its light based slicing. Static objects
    /// can be merged into the same cells (their light baked into the vertex colour), so a cell is one draw call per
    /// kind.
    ///
    /// Gothic is left-handed, Godot right-handed: z is mirrored, and triangle corners are reversed to stay front faces.
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

        private readonly float _cellSize;
        private readonly Dictionary<(int X, int Z, Kind Kind), Chunk> _chunks = new();
        private readonly Dictionary<(int X, int Z, Kind Kind), Chunk> _objectChunks = new();

        public int ChunkCount { get; private set; }
        public int TriangleCount { get; private set; }
        public int OutdoorPolygons { get; private set; }
        public int IndoorPolygons { get; private set; }

        public WorldChunkBuilder(float cellSize)
        {
            _cellSize = cellSize;
        }

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

        public void AddWorld(IMesh mesh, IReadOnlyList<int> polygonIds, TextureArrayBuilder textures,
            GroundLightSampler groundLight = null)
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

            var polygons = mesh.Polygons;
            var positions = mesh.Positions;
            var features = mesh.Features;

            foreach (var polygonId in polygonIds)
            {
                var polygon = polygons[polygonId];
                if (polygon == null)
                    continue;
                if (polygon.IsPortal || polygon.IsGhostOccluder || polygon.PositionIndices.Count < 3)
                    continue;

                // Untextured polygons are invisible helpers in Gothic (occluders, collision walls).
                var info = materials[polygon.MaterialIndex];
                if (info.Layer == 0)
                    continue;

                var chunk = GetChunk(ToGodot(positions[polygon.PositionIndices[0]]), info.Kind);
                // Colour alpha: outdoors (follows the daylight) or inside a building - a polygon of an indoor portal room
                // (sector). The outdoor flag itself is set on almost none of G1's polygons.
                var outdoor = polygon.IsSector ? 0f : 1f;
                if (outdoor > 0)
                    OutdoorPolygons++;
                else
                    IndoorPolygons++;

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
                        (light & 0xFF) / 255f, outdoor));
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
                    chunk.Indices.Add(AddCorner(p + 1));
                    chunk.Indices.Add(AddCorner(p));
                }
            }

            // The corner dedup tables are only needed while the world polygons are added.
            foreach (var chunk in _chunks.Values)
            {
                chunk.Dedup.Clear();
                chunk.Dedup.TrimExcess();
            }
        }

        /// <summary>
        /// Adds one static object's triangles in world space to the cell of its origin.
        /// </summary>
        public void AddObject(Vector3 origin, Kind kind, Vector3[] positions, Vector3[] normals, Vector2[] uvs,
            int layer, Color[] colors, int[] indices)
        {
            var chunk = GetChunk(origin, kind, ObjectFarDistance > 0 ? _objectChunks : _chunks);
            var start = chunk.Positions.Count;
            chunk.Positions.AddRange(positions);
            chunk.Normals.AddRange(normals);
            chunk.Uvs.AddRange(uvs);
            chunk.Colors.AddRange(colors);
            for (var i = 0; i < positions.Length; i++)
                chunk.Layers.Add(new Vector2(layer, 0));
            foreach (var index in indices)
                chunk.Indices.Add(start + index);
        }

        public int CollisionTriangleCount { get; private set; }

        // Objects Gothic lets collide (cdStatic), per cell: world-space triangle corners.
        private readonly Dictionary<(int X, int Z), List<Vector3>> _objectCollision = new();

        public void AddObjectCollision(Vector3 origin, Vector3[] positions, int[] indices)
        {
            var key = ((int)Math.Floor(origin.X / _cellSize), (int)Math.Floor(origin.Z / _cellSize));
            if (!_objectCollision.TryGetValue(key, out var faces))
                _objectCollision[key] = faces = new List<Vector3>();
            foreach (var index in indices)
                faces.Add(positions[index]);
        }

        /// <summary>
        /// World triangles at least this big (m²) become occluders per cell (0 = no occlusion culling): walls,
        /// palisades, terrain - what hides the camp behind it.
        /// </summary>
        public float OccluderMinArea { get; set; }
        public int OccluderTriangleCount { get; private set; }

        /// <summary>
        /// Objects further than this (metres) are not drawn, like Gothic's object far clip; 0 = merged into the
        /// world cells and drawn as far as the world. Objects then get their own cell meshes.
        /// </summary>
        public float ObjectFarDistance { get; set; }

        /// <summary>
        /// Alpha tested objects (trees, bushes, fences) hide at this distance instead (0: like the others): they were
        /// the biggest GPU cost on the Quest when drawn far, overdraw of their leaves on a tile based GPU.
        /// </summary>
        public float AlphaObjectFarDistance { get; set; }

        public bool CompressAttributes { get; set; }

        /// <summary>Kinds left out (benchmark: their share of the GPU time).</summary>
        public HashSet<Kind> SkippedKinds { get; } = new();

        public void CreateNodes(Node3D parent, Func<Kind, Material> getMaterial, bool isCollisionEnabled = false)
        {
            StaticBody3D body = null;
            if (isCollisionEnabled)
            {
                body = new StaticBody3D { Name = "WorldCollision" };
                parent.AddChild(body);
            }

            foreach (var ((x, z, kind), chunk, isObject) in _chunks.Select(c => (c.Key, c.Value, false))
                         .Concat(_objectChunks.Select(c => (c.Key, c.Value, true))))
            {
                if (SkippedKinds.Contains(kind))
                    continue;

                // Object cells are drawn up to the object far distance, measured from the cell's centre.
                var center = Vector3.Zero;
                if (isObject)
                {
                    var bounds = new Aabb(chunk.Positions[0], Vector3.Zero);
                    foreach (var position in chunk.Positions)
                        bounds = bounds.Expand(position);
                    center = bounds.GetCenter();
                    for (var i = 0; i < chunk.Positions.Count; i++)
                        chunk.Positions[i] -= center;
                }

                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = chunk.Positions.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = chunk.Normals.ToArray();
                arrays[(int)Mesh.ArrayType.Color] = chunk.Colors.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = chunk.Uvs.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV2] = chunk.Layers.ToArray();
                arrays[(int)Mesh.ArrayType.Index] = chunk.Indices.ToArray();

                var arrayMesh = new ArrayMesh();
                // --compress-meshes: 16-bit positions (per chunk bounds), octahedral normals, half-float UVs - less video
                // memory and bandwidth for the Quest's tile based GPU.
                arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
                    CompressAttributes ? Mesh.ArrayFormat.FlagCompressAttributes : 0);
                arrayMesh.SurfaceSetMaterial(0, getMaterial(kind));

                var instance = new MeshInstance3D
                {
                    Name = $"{(isObject ? "Objects" : kind)}_{x}_{z}",
                    Mesh = arrayMesh,
                    Position = center,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                // The kind finds the shared material again when the cell comes from the world cache.
                instance.SetMeta(_kindMeta, (int)kind);
                if (isObject)
                {
                    var far = kind == Kind.AlphaTest && AlphaObjectFarDistance > 0 ? AlphaObjectFarDistance : ObjectFarDistance;
                    instance.VisibilityRangeEnd = far + _cellSize * 0.5f;
                    instance.VisibilityRangeEndMargin = 2f;
                }
                parent.AddChild(instance);

                if (OccluderMinArea > 0 && !isObject && kind == Kind.Opaque)
                    AddOccluder(parent, chunk, $"{x}_{z}");

                // Collision for the world itself; merged objects (millions of triangles in G2) are left out here.
                if (body != null && kind != Kind.Water && !isObject)
                {
                    var faces = new Vector3[chunk.Indices.Count];
                    for (var i = 0; i < faces.Length; i++)
                        faces[i] = chunk.Positions[chunk.Indices[i]] + center;
                    AddCollision(body, faces);
                }

                ChunkCount++;
                TriangleCount += chunk.Indices.Count / 3;
            }

            if (body != null)
            {
                foreach (var faces in _objectCollision.Values)
                    AddCollision(body, faces.ToArray());
            }

            _chunks.Clear();
            _objectChunks.Clear();
            _objectCollision.Clear();
        }

        private const string _kindMeta = "kind";

        /// <summary>Triangles per collision shape at most: what Jolt builds in one frame when it is added late.</summary>
        public const int MaxShapeTriangles = 512;

        /// <summary>Collision in pieces, each with its bounds and size for <see cref="CollisionStreamer"/>.</summary>
        private void AddCollision(StaticBody3D body, Vector3[] faces)
        {
            var triangles = faces.Length / 3;
            for (var first = 0; first < triangles; first += MaxShapeTriangles)
            {
                var count = Math.Min(MaxShapeTriangles, triangles - first);
                var piece = new Vector3[count * 3];
                Array.Copy(faces, first * 3, piece, 0, piece.Length);
                var bounds = new Aabb(piece[0], Vector3.Zero);
                foreach (var corner in piece)
                    bounds = bounds.Expand(corner);
                var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
                shape.SetFaces(piece);
                var node = new CollisionShape3D { Shape = shape };
                node.SetMeta(CollisionStreamer.BoundsMeta, bounds);
                node.SetMeta(CollisionStreamer.TrianglesMeta, count);
                body.AddChild(node);
                CollisionTriangleCount += count;
            }
        }

        /// <summary>Gives every cell mesh below the node the material of its kind (null: none, before saving).</summary>
        public static void SetMaterials(Node parent, Func<Kind, Material> getMaterial)
        {
            foreach (var child in parent.GetChildren())
            {
                if (child is MeshInstance3D { Mesh: ArrayMesh mesh } instance && instance.HasMeta(_kindMeta))
                    mesh.SurfaceSetMaterial(0, getMaterial((Kind)instance.GetMeta(_kindMeta).AsInt32()));
                SetMaterials(child, getMaterial);
            }
        }

        private void AddOccluder(Node3D parent, Chunk chunk, string name)
        {
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            var minDoubleArea = OccluderMinArea * 2f;
            for (var i = 0; i < chunk.Indices.Count; i += 3)
            {
                var a = chunk.Positions[chunk.Indices[i]];
                var b = chunk.Positions[chunk.Indices[i + 1]];
                var c = chunk.Positions[chunk.Indices[i + 2]];
                if ((b - a).Cross(c - a).Length() < minDoubleArea)
                    continue;
                indices.Add(vertices.Count);
                vertices.Add(a);
                indices.Add(vertices.Count);
                vertices.Add(b);
                indices.Add(vertices.Count);
                vertices.Add(c);
            }

            if (indices.Count == 0)
                return;

            var occluder = new ArrayOccluder3D();
            occluder.SetArrays(vertices.ToArray(), indices.ToArray());
            parent.AddChild(new OccluderInstance3D { Name = $"Occluder_{name}", Occluder = occluder });
            OccluderTriangleCount += indices.Count / 3;
        }

        private Chunk GetChunk(Vector3 position, Kind kind) => GetChunk(position, kind, _chunks);

        private Chunk GetChunk(Vector3 position, Kind kind, Dictionary<(int X, int Z, Kind Kind), Chunk> chunks)
        {
            var key = ((int)Math.Floor(position.X / _cellSize), (int)Math.Floor(position.Z / _cellSize), kind);
            if (!chunks.TryGetValue(key, out var chunk))
            {
                chunk = new Chunk();
                chunks[key] = chunk;
            }

            return chunk;
        }
    }
}
