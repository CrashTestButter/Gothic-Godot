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

        /// <summary>Time spent decoding textures, and building / uploading the array (load log).</summary>
        public double DecodeMs { get; private set; }
        public double BuildMs { get; private set; }

        /// <summary>Layers that are in the built array (spare ones included); later ones beyond the spares are not.</summary>
        public int BuiltLayerCount { get; private set; } = int.MaxValue;

        /// <summary>
        /// Empty layers at the end of the array for textures first seen after loading (a reward item, later an NPC
        /// met far from the start): filled in place, so their surfaces stay on the shared materials.
        /// </summary>
        public int SpareLayers { get; set; } = 64;

        /// <summary>Spare layers filled after loading.</summary>
        public int LateLayerCount { get; private set; }

        private Texture2DArray _array;
        private Image.CompressMode? _mode;
        private int _nextSpare = int.MaxValue;

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
            var image = layer > 0 && layer < _images.Count ? _images[layer] : null;
            image ??= layer > 0 ? LoadImage(textureName, out _) : null;
            var texture = image != null ? ImageTexture.CreateFromImage(image) : null;
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
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var image = LoadImage(textureName, out hasAlpha);
            DecodeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (image != null && _array != null && _nextSpare < BuiltLayerCount)
            {
                // After loading: into the next spare layer, in the array's format.
                layer = _nextSpare++;
                if (_mode != null)
                    Compress(image, _mode.Value);
                _array.UpdateLayer(image, layer);
                LateLayerCount++;
                GD.Print($"[Textures] {textureName} came after loading: spare layer {layer}, " +
                         $"{BuiltLayerCount - _nextSpare} left");
            }
            else if (image != null)
            {
                layer = _images.Count;
                _images.Add(image);
            }
            else
            {
                MissingCount++;
                MissingNames.Add(textureName);
            }

            _layers[textureName] = layer;
            _hasAlpha[textureName] = hasAlpha;
            return layer;
        }

        /// <summary>
        /// Compression of the array's layers: none (RGBA8, 4 bytes a texel), etc2 or astc (Quest GPUs, 0.5 / 1 byte),
        /// s3tc (desktop GPUs). Compressed on the CPU at load time, all layers in one format.
        /// </summary>
        public string Compression { get; set; } = "none";

        /// <summary>Where Build saves the layers for the world cache when there are new ones (null: not saved).</summary>
        public string CachePath { get; set; }

        /// <summary>Time spent reading the cached layers.</summary>
        public double CacheReadMs { get; private set; }

        private const int _cacheFormat = 1;
        private int _cachedLayerCount = -1;

        /// <summary>
        /// The layers of an earlier start, already decoded, scaled and compressed: known names keep their layers, new
        /// textures are added behind them (and saved with them at Build).
        /// </summary>
        public void LoadCache(string path)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var reader = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            if (reader.ReadInt32() != _cacheFormat || reader.ReadInt32() != _sliceSize)
            {
                GD.PrintErr($"[Cache] {path} has another format or layer size, textures are decoded");
                return;
            }

            var layerCount = reader.ReadInt32();
            _images.Clear();
            for (var i = 0; i < layerCount; i++)
            {
                var format = (Image.Format)reader.ReadInt32();
                var hasMipmaps = reader.ReadBoolean();
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                var data = reader.ReadBytes(reader.ReadInt32());
                _images.Add(Image.CreateFromData(width, height, hasMipmaps, format, data));
            }

            var nameCount = reader.ReadInt32();
            for (var i = 0; i < nameCount; i++)
            {
                var name = reader.ReadString();
                var layer = reader.ReadInt32();
                _layers[name] = layer;
                _hasAlpha[name] = reader.ReadBoolean();
                if (layer != 0)
                    continue;
                MissingCount++;
                MissingNames.Add(name);
            }

            _cachedLayerCount = layerCount;
            CacheReadMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void SaveCache(string path)
        {
            using var writer = new System.IO.BinaryWriter(System.IO.File.Create(path));
            writer.Write(_cacheFormat);
            writer.Write(_sliceSize);
            writer.Write(_images.Count);
            foreach (var image in _images)
            {
                writer.Write((int)image.GetFormat());
                writer.Write(image.HasMipmaps());
                writer.Write(image.GetWidth());
                writer.Write(image.GetHeight());
                var data = image.GetData();
                writer.Write(data.Length);
                writer.Write(data);
            }

            writer.Write(_layers.Count);
            foreach (var (name, layer) in _layers)
            {
                writer.Write(name);
                writer.Write(layer);
                writer.Write(_hasAlpha[name]);
            }
        }

        /// <summary>A texture decoded and scaled to the layer size with mipmaps; null if it can't be read.</summary>
        private Image LoadImage(string textureName, out bool hasAlpha)
        {
            hasAlpha = false;
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
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // All layers as RGBA: Compress alone picks RGB or RGBA per image (ETC2_RGB8 / ETC2_RGBA8, DXT1 / DXT5) and an
        // array takes only one format.
        private static void Compress(Image image, Image.CompressMode mode) =>
            image.CompressFromChannels(mode, Image.UsedChannels.Rgba);

        public Texture2DArray Build()
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return BuildArray();
            }
            finally
            {
                BuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }

        private Texture2DArray BuildArray()
        {
            var mode = _mode = Compression switch
            {
                "etc2" => Image.CompressMode.Etc2,
                "astc" => Image.CompressMode.Astc,
                "s3tc" => Image.CompressMode.S3Tc,
                _ => (Image.CompressMode?)null
            };
            if (mode != null)
            {
                foreach (var image in _images)
                    if (!image.IsCompressed())
                        Compress(image, mode.Value);
            }

            if (CachePath != null && _images.Count != _cachedLayerCount)
                SaveCache(CachePath);

            _nextSpare = _images.Count;
            if (SpareLayers > 0)
            {
                var spare = (Image)_images[0].Duplicate();
                if (mode != null && !spare.IsCompressed())
                    Compress(spare, mode.Value);
                for (var i = 0; i < SpareLayers; i++)
                    _images.Add(spare);
            }

            BuiltLayerCount = _images.Count;
            var array = _array = new Texture2DArray();
            array.CreateFromImages(_images);
            // The GPU has them now: the decoded images (G2: ~180 MB) are let go; late textures decode again.
            for (var i = 0; i < _images.Count; i++)
                _images[i] = null;
            return array;
        }
    }
}
