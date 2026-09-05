using System.Buffers.Binary;

namespace PGAssetTool.Core.Import.Audio;

/// A RIFF/WAVE file decoded to 16-bit samples.
///
/// Wider and floating-point input is accepted and narrowed here rather than refused, since an editor
/// will happily hand back 24-bit or float.
public static class WaveFile
{
    private const int Pcm = 1;
    private const int Float = 3;
    private const int Extensible = 0xFFFE;

    public static PcmSound Read(string path) => Parse(File.ReadAllBytes(path), path);

    public static PcmSound Parse(byte[] raw, string what)
    {
        if (raw.Length < 12
            || System.Text.Encoding.ASCII.GetString(raw, 0, 4) != "RIFF"
            || System.Text.Encoding.ASCII.GetString(raw, 8, 4) != "WAVE")
            throw new InvalidDataException($"'{what}' is not a WAV file.");

        int format = 0, channels = 0, frequency = 0, bits = 0, dataAt = -1, dataLength = 0;

        // Chunks are walked rather than assumed: writers put LIST, fact and cue chunks before the
        // data, so the payload is not reliably 44 bytes in.
        for (var at = 12; at + 8 <= raw.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(raw, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 4));
            if (size < 0 || at + 8 + size > raw.Length) size = raw.Length - at - 8;

            if (id == "fmt " && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 8));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 10));
                frequency = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 22));

                // WAVE_FORMAT_EXTENSIBLE hides the real format in a GUID whose first two bytes are
                // the plain format tag.
                if (format == Extensible && size >= 40)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 32));
            }
            else if (id == "data")
            {
                dataAt = at + 8;
                dataLength = size;
            }

            at += 8 + size + (size & 1);   // chunks are padded to an even length
        }

        if (dataAt < 0 || channels == 0 || frequency == 0)
            throw new InvalidDataException($"'{what}' has no usable fmt and data chunks.");
        if (format is not (Pcm or Float))
            throw new NotSupportedException(
                $"'{what}' is compressed (WAV format tag {format}). Save it as uncompressed PCM.");

        return new PcmSound(Narrow(raw.AsSpan(dataAt, dataLength), format, bits, what), channels, frequency);
    }

    private static short[] Narrow(ReadOnlySpan<byte> data, int format, int bits, string what)
    {
        if (format == Float && bits == 32)
        {
            var samples = new short[data.Length / 4];
            for (int i = 0; i < samples.Length; i++)
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
                samples[i] = (short)Math.Clamp(Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
            }
            return samples;
        }

        switch (bits)
        {
            case 16:
            {
                var samples = new short[data.Length / 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
                return samples;
            }
            case 8:
            {
                // Eight-bit WAV is unsigned, centred on 128.
                var samples = new short[data.Length];
                for (int i = 0; i < samples.Length; i++) samples[i] = (short)((data[i] - 128) << 8);
                return samples;
            }
            case 24:
            {
                var samples = new short[data.Length / 3];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(data[i * 3 + 1] | data[i * 3 + 2] << 8);
                return samples;
            }
            case 32:
            {
                var samples = new short[data.Length / 4];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) >> 16);
                return samples;
            }
            default:
                throw new NotSupportedException($"'{what}' is {bits}-bit, which is not a WAV depth this reads.");
        }
    }
}
