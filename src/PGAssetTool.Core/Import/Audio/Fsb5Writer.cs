using System.Buffers.Binary;

namespace PGAssetTool.Core.Import.Audio;

/// Writes a single-sample PCM16 FSB5 — the container a Unity AudioClip points at.
///
/// The game ships Vorbis and ADPCM only, so nothing here reproduces what it already has. PCM is the
/// one format its bundled FMOD runtime accepts from outside: an fsbankcl `fadpcm` build was rejected
/// in-game as an unsupported format, while `pcm` played. Encoding Vorbis to match would mean
/// carrying an encoder and matching FMOD's exact setup headers, for a file the player never notices
/// is larger.
///
/// Verified byte for byte against `fsbankcl -no_guid` output across mono and stereo, table and
/// off-table sample rates.
public static class Fsb5Writer
{
    /// The rates FSB5 can name in the sample header itself. Index zero means "a FREQUENCY chunk
    /// carries it instead", which is how 31000 Hz — which the game does use — has to be written.
    private static readonly int[] Rates = [0, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

    private const int Pcm16Codec = 2;
    private const int FrequencyChunkType = 2;
    private const int HeaderSize = 60;

    /// Sample data starts on a 32-byte boundary because the header stores its offset divided by 32.
    private const int DataAlignment = 32;

    /// FMOD reserves sixteen frames of silence past the last real one before aligning. Measured, not
    /// documented: with everything else matching fsbankcl exactly, dataSize came out short by 32
    /// bytes per channel. Presumably decoder lookahead.
    private const int GuardFrames = 16;

    /// <param name="name">
    /// The clip's name, written into a name table. Every FSB5 FMOD produces starts its sample data
    /// on a 32-byte boundary, and the name table is the only field that can absorb the padding, so
    /// one is always written even though the game's own clips carry none.
    /// </param>
    public static byte[] Write(short[] samples, int channels, int frequency, string name)
    {
        if (channels is not (1 or 2))
            throw new NotSupportedException(
                $"{channels} channels would need a CHANNELS chunk; the header itself only distinguishes mono from stereo.");
        if (samples.Length % channels != 0)
            throw new ArgumentException($"{samples.Length} values do not divide into {channels} channels.");

        var frames = samples.Length / channels;
        var rate = Array.IndexOf(Rates, frequency);
        var carriesRate = rate < 0;

        var sampleHeaderSize = carriesRate ? 16 : 8;
        var nameTable = NameTable(name, HeaderSize + sampleHeaderSize);
        var dataSize = Align((frames + GuardFrames) * channels * sizeof(short));

        var file = new byte[HeaderSize + sampleHeaderSize + nameTable.Length + dataSize];
        var span = file.AsSpan();

        "FSB5"u8.CopyTo(span);
        Write32(span[4..], 1);                      // version
        Write32(span[8..], 1);                      // one sample in the bank
        Write32(span[12..], sampleHeaderSize);
        Write32(span[16..], nameTable.Length);
        Write32(span[20..], dataSize);
        Write32(span[24..], Pcm16Codec);
        // Bytes 28..59 are a zero field, a hash and a GUID. fsbankcl fills the last two unless it is
        // told not to; leaving them zero is what -no_guid produces and keeps the output reproducible.

        var at = HeaderSize;
        Write64(span[at..], SampleInfo(frames, channels, carriesRate ? 0 : rate, dataOffset: 0, carriesRate));
        at += 8;

        if (carriesRate)
        {
            Write32(span[at..], Chunk(FrequencyChunkType, size: 4, more: false));
            Write32(span[(at + 4)..], frequency);
            at += 8;
        }

        nameTable.CopyTo(span[at..]);
        at += nameTable.Length;

        foreach (var sample in samples)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[at..], sample);
            at += sizeof(short);
        }

        // The tail up to the alignment boundary stays zero, which is silence.
        return file;
    }

    /// The sample header packs everything about the clip into one 64-bit word.
    private static ulong SampleInfo(int frames, int channels, int rate, int dataOffset, bool hasChunks)
        => (ulong)frames << 34
         | (ulong)(dataOffset / DataAlignment) << 6
         | (ulong)(channels - 1) << 5
         | (ulong)rate << 1
         | (hasChunks ? 1UL : 0UL);

    private static uint Chunk(int type, int size, bool more)
        => (uint)type << 25 | (uint)(size & 0xFFFFFF) << 1 | (more ? 1u : 0u);

    /// A four-byte offset per name, then the names null-terminated, grown until the sample data that
    /// follows the table begins on a 32-byte boundary.
    private static byte[] NameTable(string name, int precedingBytes)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(name);
        var minimum = 4 + text.Length + 1;
        var table = new byte[Align(precedingBytes + minimum) - precedingBytes];
        Write32(table, 4);
        text.CopyTo(table, 4);
        return table;
    }
    private static int Align(int value, int to = DataAlignment) => (value + to - 1) / to * to;

    private static void Write32(Span<byte> at, int value)
        => BinaryPrimitives.WriteUInt32LittleEndian(at, (uint)value);

    private static void Write32(Span<byte> at, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(at, value);

    private static void Write64(Span<byte> at, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(at, value);
}
