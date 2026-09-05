using System.Buffers.Binary;
using PGAssetTool.Core.Import.Audio;

namespace PGAssetTool.Core.Tests;

public class WaveFileTests
{
    /// <param name="extra">Chunks placed before the data, which real writers do emit.</param>
    private static byte[] Wav(int format, int bits, int channels, int frequency, byte[] data, params byte[][] extra)
    {
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, (ushort)format);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), frequency);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)bits);

        var body = new MemoryStream();
        void Chunk(string id, byte[] payload)
        {
            body.Write(System.Text.Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
            body.Write(size);
            body.Write(payload);
            if (payload.Length % 2 == 1) body.WriteByte(0);
        }

        Chunk("fmt ", fmt);
        foreach (var (i, chunk) in extra.Index()) Chunk(i == 0 ? "LIST" : "fact", chunk);
        Chunk("data", data);

        var file = new MemoryStream();
        file.Write("RIFF"u8);
        var riffSize = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(riffSize, (int)body.Length + 4);
        file.Write(riffSize);
        file.Write("WAVE"u8);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private static byte[] Pcm16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    [Fact]
    public void SixteenBitPcmComesBackUnchanged()
    {
        var wave = WaveFile.Parse(Wav(1, 16, 1, 44100, Pcm16(0, 1000, -1000, short.MinValue)), "t");

        Assert.Equal([0, 1000, -1000, short.MinValue], wave.Samples);
        Assert.Equal(1, wave.Channels);
        Assert.Equal(44100, wave.Frequency);
        Assert.Equal(4, wave.Frames);
    }

    [Fact]
    public void ChunksBeforeTheDataAreSteppedOverRatherThanCountedAsAudio()
    {
        // ffmpeg writes a LIST chunk, so the payload is not reliably 44 bytes in.
        var wave = WaveFile.Parse(
            Wav(1, 16, 1, 32000, Pcm16(7, 8), [1, 2, 3], [4, 5, 6, 7]), "t");

        Assert.Equal([7, 8], wave.Samples);
        Assert.Equal(32000, wave.Frequency);
    }

    [Fact]
    public void StereoFramesArePairsNotSamples()
    {
        var wave = WaveFile.Parse(Wav(1, 16, 2, 48000, Pcm16(1, 2, 3, 4, 5, 6)), "t");

        Assert.Equal(2, wave.Channels);
        Assert.Equal(3, wave.Frames);
        Assert.Equal(3f / 48000, wave.Seconds, 6);
    }

    [Theory]
    [InlineData(8, new byte[] { 128, 255, 0 }, new short[] { 0, 32512, -32768 })]
    [InlineData(24, new byte[] { 0, 0, 16, 0, 0, 0xF0 }, new short[] { 0x1000, unchecked((short)0xF000) })]
    public void NarrowerAndWiderDepthsAreConvertedRatherThanRefused(int bits, byte[] data, short[] expected)
        => Assert.Equal(expected, WaveFile.Parse(Wav(1, bits, 1, 44100, data), "t").Samples);

    [Fact]
    public void FloatSamplesAreScaledIntoSixteenBits()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(data, 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), -0.5f);

        Assert.Equal([short.MaxValue, -16384], WaveFile.Parse(Wav(3, 32, 1, 44100, data), "t").Samples);
    }

    [Fact]
    public void ExtensibleHidesTheRealFormatInItsGuid()
    {
        // WAVE_FORMAT_EXTENSIBLE's tag is 0xFFFE; the actual format is the first field of the GUID.
        var fmt = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), 44100);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), 1);   // PCM, inside the GUID

        var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(new byte[4]);
        file.Write("WAVE"u8);
        file.Write("fmt "u8);
        file.Write(BitConverter.GetBytes(40));
        file.Write(fmt);
        file.Write("data"u8);
        file.Write(BitConverter.GetBytes(4));
        file.Write(Pcm16(11, 22));

        Assert.Equal([11, 22], WaveFile.Parse(file.ToArray(), "t").Samples);
    }

    [Fact]
    public void ACompressedWavSaysSoRatherThanDecodingToNoise()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => WaveFile.Parse(Wav(0x11, 4, 1, 44100, [1, 2, 3, 4]), "beep.wav"));
        Assert.Contains("uncompressed PCM", error.Message);
    }

    [Fact]
    public void SomethingThatIsNotAWavIsRejectedByName()
        => Assert.Contains("beep.mp3",
            Assert.Throws<InvalidDataException>(() => WaveFile.Parse([1, 2, 3, 4], "beep.mp3")).Message);
}
