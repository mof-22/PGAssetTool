using System.Text;
using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// The CRC the game checks a bundle's contents against.
///
/// The arithmetic is checked here against published vectors; that it is taken over the concatenated
/// decompressed block data was settled against 5,174 of the game's own bundles, which no test can
/// carry. Getting either half wrong makes a written bundle fail its check and be thrown away, with
/// nothing the player sees.
public class BundleCrcTests
{
    /// The standard CRC-32 check value: "123456789" hashes to 0xCBF43926.
    [Fact]
    public void ItIsTheUsualCrc32()
        => Assert.Equal(0xCBF43926u, BundleCrc.Of("123456789"u8));

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("abc", 0x352441C2u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void AndItAgreesOnTheRest(string text, uint expected)
        => Assert.Equal(expected, BundleCrc.Of(Encoding.ASCII.GetBytes(text)));

    /// Nothing hashes to zero, which is what the game reports for a bundle whose blocks will not
    /// decompress: it has no data to hash rather than data that disagrees.
    [Fact]
    public void NothingHashesToZero()
        => Assert.Equal(0u, BundleCrc.Of([]));

    /// Hashing in pieces has to come to the same answer, because a bundle is hashed one block at a
    /// time rather than gathered first — a 6MB weapon-map bundle decompresses to 99MB.
    [Fact]
    public void HashingInPiecesAgreesWithHashingAtOnce()
    {
        var whole = new byte[1000];
        new Random(1).NextBytes(whole);

        var running = new BundleCrc.Running();
        running.Add(whole.AsSpan(0, 1));
        running.Add(whole.AsSpan(1, 299));
        running.Add(whole.AsSpan(300, 700));

        Assert.Equal(BundleCrc.Of(whole), running.Value);
    }

    /// And a piece on its own is not the whole, or the test above would pass on any arithmetic.
    [Fact]
    public void APrefixDoesNotHashLikeTheWhole()
    {
        var whole = new byte[1000];
        new Random(2).NextBytes(whole);

        Assert.NotEqual(BundleCrc.Of(whole), BundleCrc.Of(whole.AsSpan(0, 300)));
    }

    [Fact]
    public void ABundleThatIsNotThereIsNotAnAnswer()
        => Assert.Throws<FileNotFoundException>(() =>
            BundleCrc.OfBundle(Path.Combine(Path.GetTempPath(), $"pgat-missing-{Guid.NewGuid():N}")));
}
