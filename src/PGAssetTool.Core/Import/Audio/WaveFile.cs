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

            // A writer that streamed its output and never went back to patch the header leaves this
            // at zero. Fmod5Sharp does exactly that, so a WAV rebuilt from a bank claims to hold no
            // audio at all — which is silence rather than a refusal, and worth stepping around.
            if (id == "data" && size == 0) size = raw.Length - at - 8;

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

    /// The sound as a RIFF file: the one format Windows will play from memory without help.
    public static byte[] Write(PcmSound sound)
    {
        var data = sound.Samples.Length * sizeof(short);
        var file = new byte[44 + data];
        var at = file.AsSpan();

        "RIFF"u8.CopyTo(at);
        BinaryPrimitives.WriteInt32LittleEndian(at[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(at[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(at[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(at[20..], Pcm);
        BinaryPrimitives.WriteUInt16LittleEndian(at[22..], (ushort)sound.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(at[24..], sound.Frequency);
        BinaryPrimitives.WriteInt32LittleEndian(at[28..], sound.Frequency * sound.Channels * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(at[32..], (ushort)(sound.Channels * sizeof(short)));
        BinaryPrimitives.WriteUInt16LittleEndian(at[34..], 16);
        "data"u8.CopyTo(at[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(at[40..], data);

        for (var i = 0; i < sound.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(at[(44 + i * 2)..], sound.Samples[i]);

        return file;
    }

    /// Fills in the two length fields a RIFF header carries, in place, when they were left at zero.
    ///
    /// Fmod5Sharp rebuilds a sample by writing the header first and the audio after it, and never
    /// returns to say how much it wrote. A player that seeks by those lengths — Windows Media Player
    /// does — then finds a file that declares itself empty and plays nothing, while one that reads
    /// to the end of the stream, like VLC, never notices. Only zeroes are touched, so a file that
    /// already states its lengths is returned exactly as it came.
    public static byte[] WithLengthsFilledIn(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)) return wav;

        if (BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)) == 0)
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);

        for (var at = 12; at + 8 <= wav.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(at + 4));
            if (wav.AsSpan(at, 4).SequenceEqual("data"u8) && size == 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(at + 4), wav.Length - at - 8);
                return wav;
            }

            if (size <= 0 || at + 8 + size > wav.Length) return wav;
            at += 8 + size + (size & 1);
        }
        return wav;
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
