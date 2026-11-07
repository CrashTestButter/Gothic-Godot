using System;
using System.Collections.Generic;
using System.IO;
using Gothic.Core.Domain.Audio;
using Godot;
using ZenKit;

namespace GothicGodot.Audio
{
    /// <summary>
    /// Gothic's WAV files (sounds, speech) as Godot streams: PCM as it is, IMA ADPCM (most speech) decoded with
    /// Gothic-Unity's ImaadpcmDecoderDomain from the shared core. Streams are cached by name.
    /// </summary>
    public static class GothicSound
    {
        private const int _formatPcm = 1;
        private const int _formatImaAdpcm = 0x11;

        private static readonly Dictionary<string, AudioStreamWav> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static AudioStreamWav Load(Vfs vfs, string name)
        {
            var key = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            AudioStreamWav stream = null;
            var node = vfs.Find($"{key}.WAV");
            if (node != null)
            {
                try
                {
                    stream = FromWav(node.Buffer.Bytes);
                }
                catch (Exception e)
                {
                    GD.PushWarning($"[Sound] {key}.WAV can't be decoded: {e.Message}");
                }
            }

            _cache[key] = stream;
            return stream;
        }

        private static AudioStreamWav FromWav(byte[] bytes)
        {
            var format = ReadFormat(bytes, out var channels, out var sampleRate, out var bits, out var dataOffset,
                out var dataLength);
            if (format == _formatImaAdpcm)
            {
                bytes = new ImaadpcmDecoderDomain().Decode(bytes);
                format = ReadFormat(bytes, out channels, out sampleRate, out bits, out dataOffset, out dataLength);
            }

            if (format != _formatPcm || (bits != 8 && bits != 16))
                throw new InvalidDataException($"unsupported WAV format {format} with {bits} bits");

            var data = new byte[dataLength];
            Array.Copy(bytes, dataOffset, data, 0, Math.Min(dataLength, bytes.Length - dataOffset));
            if (bits == 8)
            {
                // Godot's 8-bit WAV data is signed, WAV's is unsigned.
                for (var i = 0; i < data.Length; i++)
                    data[i] = (byte)(data[i] - 128);
            }

            return new AudioStreamWav
            {
                Format = bits == 8 ? AudioStreamWav.FormatEnum.Format8Bits : AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = sampleRate,
                Stereo = channels == 2,
                Data = data
            };
        }

        private static int ReadFormat(byte[] bytes, out int channels, out int sampleRate, out int bits,
            out int dataOffset, out int dataLength)
        {
            channels = sampleRate = bits = dataOffset = dataLength = 0;
            var format = 0;
            var position = 12; // "RIFF" size "WAVE"
            while (position + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
                var size = BitConverter.ToInt32(bytes, position + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(bytes, position + 8);
                    channels = BitConverter.ToInt16(bytes, position + 10);
                    sampleRate = BitConverter.ToInt32(bytes, position + 12);
                    bits = BitConverter.ToInt16(bytes, position + 22);
                }
                else if (id == "data")
                {
                    dataOffset = position + 8;
                    dataLength = Math.Min(size, bytes.Length - dataOffset);
                    break;
                }

                position += 8 + size + (size & 1);
            }

            return format;
        }
    }
}
