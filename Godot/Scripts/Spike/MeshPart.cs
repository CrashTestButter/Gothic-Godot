using System.Collections.Generic;
using Godot;
using ZenKit;

namespace GothicGodot.Spike
{
    /// <summary>
    /// One texture's triangles of a Gothic mesh, already in Godot coordinates (metres, z mirrored, corners reversed).
    /// </summary>
    public class MeshPart
    {
        public WorldChunkBuilder.Kind Kind;
        public int Layer;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] Uvs;
        public int[] Indices;

        public static Vector3 ToGodot(System.Numerics.Vector3 gothic) => new(gothic.X / 100f, gothic.Y / 100f, -gothic.Z / 100f);

        public static Basis ToGodotBasis(float m11, float m12, float m13, float m21, float m22, float m23, float m31,
            float m32, float m33) => new(
            new Vector3(m11, m21, -m31),
            new Vector3(m12, m22, -m32),
            new Vector3(-m13, -m23, m33));

        /// <summary>
        /// A Gothic node matrix (rotation rows, translation in the 4th column, centimetres) as a Godot transform.
        /// </summary>
        public static Transform3D ToGodot(System.Numerics.Matrix4x4 m) => new(
            ToGodotBasis(m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33),
            new Vector3(m.M14 / 100f, m.M24 / 100f, -m.M34 / 100f));

        public static void AddFromMrm(IMultiResolutionMesh mrm, TextureArrayBuilder textures, Transform3D transform,
            List<MeshPart> parts)
        {
            var positions = mrm.Positions;
            foreach (var subMesh in mrm.SubMeshes)
            {
                var triangles = subMesh.Triangles;
                if (triangles.Count == 0)
                    continue;

                var layer = textures.GetLayer(subMesh.Material.Texture, out var hasAlpha);
                if (layer == 0)
                    continue;

                var wedges = subMesh.Wedges;
                var part = new MeshPart
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
                    part.Positions[i] = transform * ToGodot(positions[wedge.Index]);
                    part.Normals[i] = (transform.Basis * new Vector3(wedge.Normal.X, wedge.Normal.Y, -wedge.Normal.Z))
                        .Normalized();
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
        }
    }
}
