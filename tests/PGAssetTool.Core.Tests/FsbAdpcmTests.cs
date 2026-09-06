using System.Buffers.Binary;
using PGAssetTool.Core.Import.Audio;

namespace PGAssetTool.Core.Tests;

/// The layout is not documented anywhere reachable and was read off the game's own data: a block of
/// 36 bytes carrying 64 samples, the first four being the predictor the block starts from — which is
/// also its first sample — and then the step index. See FsbAdpcm.
public class FsbAdpcmTests
{
    /// <param name="nibbles">Written low half of each byte first; the 64th is never read.</param>
    private static byte[] Block(short predictor, byte index, params int[] nibbles)
    {
        var block = new byte[FsbAdpcm.BlockBytes];
        BinaryPrimitives.WriteInt16LittleEndian(block, predictor);
        block[2] = index;

        for (var i = 0; i < nibbles.Length && i < 63; i++)
            block[4 + i / 2] |= (byte)(i % 2 == 0 ? nibbles[i] & 0xF : (nibbles[i] & 0xF) << 4);

        return block;
    }

    [Fact]
    public void TheBlockStartsFromTheStateItCarries()
    {
        // Nibble 7 at step index 0 is the largest step up there is: 7>>3 + 7>>2 + 7>>1 + 7.
        var samples = FsbAdpcm.Decode(Block(1000, 0, 7), 1, 64);

        Assert.Equal(1000, samples[0]);
        Assert.Equal(1011, samples[1]);
    }

    [Fact]
    public void ThePredictorSaturatesInsteadOfWrapping()
    {
        // The bug this exists to avoid. Driven past full scale, the signal has to flatten off; the
        // library it replaces let it roll over to the opposite extreme, which is a loud click, and
        // the synthwaver's shot had a hundred and fifty of them.
        var climbing = Enumerable.Repeat(7, 63).ToArray();
        var samples = FsbAdpcm.Decode(Block(32000, 88, climbing), 1, 64);

        Assert.All(samples, s => Assert.True(s >= 0, $"the predictor rolled over to {s}"));
        Assert.Equal(short.MaxValue, samples[^1]);
    }

    [Fact]
    public void ItSaturatesAtTheBottomToo()
    {
        var falling = Enumerable.Repeat(15, 63).ToArray();
        var samples = FsbAdpcm.Decode(Block(-32000, 88, falling), 1, 64);

        Assert.All(samples, s => Assert.True(s <= 0, $"the predictor rolled over to {s}"));
        Assert.Equal(short.MinValue, samples[^1]);
    }

    [Fact]
    public void EachBlockStartsAgainFromItsOwnState()
    {
        // Blocks are self-contained: whatever the last one drifted to, the next says where it is.
        var two = Block(1000, 0, 7).Concat(Block(-500, 0, 7)).ToArray();
        var samples = FsbAdpcm.Decode(two, 1, 128);

        Assert.Equal(1000, samples[0]);
        Assert.Equal(-500, samples[64]);
        Assert.Equal(-489, samples[65]);
    }

    [Fact]
    public void TwoChannelsAreOneBlockEachInTurn()
    {
        var stereo = Block(1000, 0, 7).Concat(Block(-500, 0, 7)).ToArray();
        var samples = FsbAdpcm.Decode(stereo, 2, 64);

        Assert.Equal(1000, samples[0]);
        Assert.Equal(-500, samples[1]);
        Assert.Equal(1011, samples[2]);
        Assert.Equal(-489, samples[3]);
    }

    [Fact]
    public void ShortInputIsTakenAsFarAsItGoesRatherThanThrowing()
    {
        // A truncated bank is a broken file, not a reason to bring down whatever was previewing it.
        Assert.Equal(64, FsbAdpcm.Decode(Block(1000, 0, 7).AsSpan(0, 20), 1, 64).Length);
        Assert.Empty(FsbAdpcm.Decode([], 1, 0));
    }
}
