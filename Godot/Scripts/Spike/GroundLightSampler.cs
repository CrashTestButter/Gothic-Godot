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
