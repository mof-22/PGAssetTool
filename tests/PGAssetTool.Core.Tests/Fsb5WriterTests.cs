using PGAssetTool.Core.Import.Audio;

namespace PGAssetTool.Core.Tests;

/// The expected bytes here were taken from `fsbankcl -no_guid` output, which the writer reproduces
/// exactly across mono and stereo at table and off-table sample rates. They are asserted field by
/// field rather than as a blob so that a failure says which part of the container drifted.
public class Fsb5WriterTests
{
    private static uint Field(byte[] fsb, int at) => BitConverter.ToUInt32(fsb, at);

    [Fact]
    public void TheBankHeaderSaysWhatFmodExpects()
    {
        var fsb = Fsb5Writer.Write(new short[22050], channels: 1, frequency: 44100, "m44");

        Assert.Equal("FSB5", System.Text.Encoding.ASCII.GetString(fsb, 0, 4));
        Assert.Equal(1u, Field(fsb, 4));    // version
        Assert.Equal(1u, Field(fsb, 8));    // one sample in the bank
        Assert.Equal(8u, Field(fsb, 12));   // sample header, no chunks
        Assert.Equal(28u, Field(fsb, 16));  // name table, padded to align the data
        Assert.Equal(2u, Field(fsb, 24));   // PCM16

        // A GUID would go here. Leaving it zero is what -no_guid does, and what keeps two runs over
        // the same input byte-identical.
        Assert.All(fsb[28..60], b => Assert.Equal(0, b));
    }

    [Fact]
    public void SampleDataStartsOnAThirtyTwoByteBoundary()
    {
        // The header stores the data offset divided by 32, so an unaligned start is not expressible.
        foreach (var (rate, channels, name) in new[] { (44100, 1, "a"), (31000, 1, "bb"), (48000, 2, "ccc") })
        {
            var fsb = Fsb5Writer.Write(new short[1000 * channels], channels, rate, name);
            var dataAt = 60 + (int)Field(fsb, 12) + (int)Field(fsb, 16);
            Assert.Equal(0, dataAt % 32);
            Assert.Equal(fsb.Length, dataAt + (int)Field(fsb, 20));
        }
    }

    [Theory]
    [InlineData(22050, 1, 44100, 0x0001588800000010ul)]
    [InlineData(16000, 1, 32000, 0x0000FA000000000Eul)]
    [InlineData(22050, 2, 44100, 0x0001588800000030ul)]
    [InlineData(24000, 2, 48000, 0x0001770000000032ul)]
    public void TheSampleHeaderPacksFramesRateAndChannelsIntoOneWord(int frames, int channels, int rate, ulong expected)
        => Assert.Equal(expected, BitConverter.ToUInt64(
            Fsb5Writer.Write(new short[frames * channels], channels, rate, "x"), 60));

    [Fact]
    public void ARateFmodCannotNameIsCarriedInAFrequencyChunk()
    {
        // 31000 Hz is not in FSB5's rate table, and the game does use it.
        var fsb = Fsb5Writer.Write(new short[15500], channels: 1, frequency: 31000, "m31");

        Assert.Equal(16u, Field(fsb, 12));
        Assert.Equal(0x0000F23000000001ul, BitConverter.ToUInt64(fsb, 60));  // rate index 0, chunks follow
        Assert.Equal(0x04000008u, Field(fsb, 68));                           // type 2 (frequency), 4 bytes, last
        Assert.Equal(31000u, Field(fsb, 72));
    }

    [Fact]
    public void SixteenFramesOfSilenceAreReservedPastTheLastRealOne()
    {
        // Undocumented, but every fsbankcl output has them and dataSize counts them.
        var fsb = Fsb5Writer.Write(new short[1024], channels: 1, frequency: 44100, "t1024");

        Assert.Equal(2080u, Field(fsb, 20));   // (1024 + 16) frames * 2 bytes, already 32-aligned
        Assert.Equal(2176, fsb.Length);
        Assert.All(fsb[(2176 - 32)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void TheNameGoesInNullTerminatedAfterItsOffset()
    {
        var fsb = Fsb5Writer.Write(new short[64], channels: 1, frequency: 44100, "shoot");
        var table = fsb.AsSpan(68, (int)Field(fsb, 16));

        Assert.Equal(4u, BitConverter.ToUInt32(table));
        Assert.Equal("shoot", System.Text.Encoding.ASCII.GetString(table[4..9]));
        Assert.Equal(0, table[9]);
    }

    [Fact]
    public void MoreThanTwoChannelsIsRefusedRatherThanWrittenWrong()
    {
        // The header has one bit for channels; anything else needs a CHANNELS chunk.
        Assert.Throws<NotSupportedException>(
            () => Fsb5Writer.Write(new short[600], channels: 6, frequency: 44100, "surround"));
    }
}
