using System.Collections.Generic;
using Godot;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Gothic lights static objects with the baked light of the world polygon below them (zCVob lightColorStat from
    /// its ground polygon). Spike version: the world's vertex light in a 2 m grid, the highest vertex below the
    /// object's position wins.
    /// </summary>
    public class GroundLightSampler
    {
        private const float _cellSize = 2f;

        private readonly Dictionary<(int, int), List<(float Y, Color Color)>> _cells = new();

        public void Add(Vector3 position, Color color)
        {
            var key = ((int)Mathf.Floor(position.X / _cellSize), (int)Mathf.Floor(position.Z / _cellSize));
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<(float, Color)>();
                _cells[key] = list;
            }
            list.Add((position.Y, color));
        }

        /// <summary>The cells for the world cache: cell count, then per cell x, z, count and (y, r, g, b, a).</summary>
        public void Save(string path)
        {
            using var writer = new System.IO.BinaryWriter(System.IO.File.Create(path));
            writer.Write(_cells.Count);
            foreach (var ((x, z), list) in _cells)
            {
                writer.Write(x);
                writer.Write(z);
                writer.Write(list.Count);
                foreach (var (y, color) in list)
                {
                    writer.Write(y);
                    writer.Write(color.R);
                    writer.Write(color.G);
                    writer.Write(color.B);
                    writer.Write(color.A);
                }
            }
        }

        public void Load(string path)
        {
            // In one read: value by value through a BinaryReader took 200 ms for G2's 11 MB.
            var data = System.IO.File.ReadAllBytes(path);
            var offset = 0;
            int NextInt() { var value = System.BitConverter.ToInt32(data, offset); offset += 4; return value; }
            float NextFloat() { var value = System.BitConverter.ToSingle(data, offset); offset += 4; return value; }

            _cells.Clear();
            var cellCount = NextInt();
            for (var c = 0; c < cellCount; c++)
            {
                var key = (NextInt(), NextInt());
                var count = NextInt();
                var list = new List<(float, Color)>(count);
                for (var i = 0; i < count; i++)
                    list.Add((NextFloat(), new Color(NextFloat(), NextFloat(), NextFloat(), NextFloat())));
                _cells[key] = list;
            }
        }

        public Color Sample(Vector3 position, Color fallback)
        {
            var cx = (int)Mathf.Floor(position.X / _cellSize);
            var cz = (int)Mathf.Floor(position.Z / _cellSize);
            var bestY = float.MinValue;
            var best = fallback;

            for (var x = cx - 1; x <= cx + 1; x++)
            for (var z = cz - 1; z <= cz + 1; z++)
            {
                if (!_cells.TryGetValue((x, z), out var list))
                    continue;

                foreach (var (y, color) in list)
                {
                    if (y <= position.Y + 0.5f && y > bestY)
                    {
                        bestY = y;
                        best = color;
                    }
                }
            }

            return best;
        }
    }
}
