using System;
using System.Collections.Generic;
using Godot;
using ZenKit;
using Image = Godot.Image;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Packs Gothic textures into one Texture2DArray with square layers of the same size (spike simplification:
    /// every texture is scaled to the layer size). Layer 0 is white for materials without a texture.
    /// </summary>
    public class TextureArrayBuilder
    {
        private readonly Vfs _vfs;
        private readonly int _sliceSize;
        private readonly Dictionary<string, int> _layers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _hasAlpha = new(StringComparer.OrdinalIgnoreCase);
        private readonly Godot.Collections.Array<Image> _images = new();

        public int LayerCount => _images.Count;

        /// <summary>Layers that are in the built array; later ones (textures first seen after loading) are not.</summary>
        public int BuiltLayerCount { get; private set; } = int.MaxValue;

        private readonly Dictionary<string, ImageTexture> _singles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A texture of its own, for surfaces made after the array was built (items from the scripts, rewards): the
        /// array has a fixed number of layers once uploaded.
        /// </summary>
        public ImageTexture GetSingle(string textureName)
        {
            if (string.IsNullOrEmpty(textureName))
                return null;
            if (_singles.TryGetValue(textureName, out var cached))
                return cached;
            var layer = GetLayer(textureName, out _);
            var texture = layer > 0 && layer < _images.Count ? ImageTexture.CreateFromImage(_images[layer]) : null;
            _singles[textureName] = texture;
            return texture;
        }
        public int MissingCount { get; private set; }
        public List<string> MissingNames { get; } = new();

        public TextureArrayBuilder(Vfs vfs, int sliceSize)
        {
            _vfs = vfs;
            _sliceSize = sliceSize;

            var white = Image.CreateEmpty(sliceSize, sliceSize, false, Image.Format.Rgba8);
            white.Fill(Colors.White);
            white.GenerateMipmaps();
            _images.Add(white);
        }

        /// <summary>Whether the texture exists (compiled), e.g. a body texture variant.</summary>
        public bool Exists(string textureName) =>
            _layers.ContainsKey(textureName) ||
            _vfs.Find($"{System.IO.Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant()}-c.tex") != null;

        public int GetLayer(string textureName, out bool hasAlpha)
        {
            hasAlpha = false;
            if (string.IsNullOrEmpty(textureName))
                return 0;

            if (_layers.TryGetValue(textureName, out var layer))
            {
                hasAlpha = _hasAlpha[textureName];
                return layer;
            }

            layer = 0;
            try
            {
                var key = $"{System.IO.Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant()}-c.tex";
                var texture = new ZenKit.Texture(_vfs, key);
                hasAlpha = texture.Format is TextureFormat.Dxt3 or TextureFormat.Dxt5 or TextureFormat.Dxt2
                    or TextureFormat.Dxt4 or TextureFormat.B8G8R8A8 or TextureFormat.R8G8B8A8
                    or TextureFormat.A8B8G8R8 or TextureFormat.A8R8G8B8 or TextureFormat.A4R4G4B4
                    or TextureFormat.A1R5G5B5;

                var level = 0;
                while (level < texture.MipmapCount - 1 && Math.Max(texture.GetWidth(level), texture.GetHeight(level)) > _sliceSize)
                    level++;

                var width = texture.GetWidth(level);
                var height = texture.GetHeight(level);
                var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, texture.GetMipmapRgba(level));
                if (width != _sliceSize || height != _sliceSize)
                    image.Resize(_sliceSize, _sliceSize, Image.Interpolation.Bilinear);
                image.GenerateMipmaps();

                layer = _images.Count;
                _images.Add(image);
            }
            catch (Exception)
            {
                MissingCount++;
                MissingNames.Add(textureName);
            }

            _layers[textureName] = layer;
            _hasAlpha[textureName] = hasAlpha;
            return layer;
        }

        public Texture2DArray Build()
        {
            BuiltLayerCount = _images.Count;
            var array = new Texture2DArray();
            array.CreateFromImages(_images);
            return array;
        }
    }
}
