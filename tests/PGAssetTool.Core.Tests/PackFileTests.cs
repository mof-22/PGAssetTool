using System.IO.Compression;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class PackFileTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pgassettool-seal").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_home, name);

    /// A zip with one entry, which is all any of this needs to be about.
    private static byte[] Zip(string contents = "the pack")
    {
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("pgmod.json").Open()))
            writer.Write(contents);
        return bytes.ToArray();
    }

    private static string Read(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry("pgmod.json")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void APlainZipIsStillAPack()
    {
        // Every pack built before this existed is one, and every pack built with protection off
        // still is. Refusing to open them would have been a worse kind of protection.
        var path = Path("plain.pgmod");
        PackFile.Write(path, Zip(), signer: null, author: "");

        Assert.Equal(Zip(), File.ReadAllBytes(path));
        Assert.Equal("the pack", Read(PackFile.Open(path)));
        Assert.Equal(SealState.Unsigned, PackFile.Inspect(path).State);
        Assert.False(PackFile.Inspect(path).Protected);
    }

    [Fact]
    public void AProtectedPackComesBackTheSameAndSaysWhoBuiltIt()
    {
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        Assert.Equal("the pack", Read(PackFile.Open(path)));

        var seal = PackFile.Inspect(path);
        Assert.True(seal.Protected);
        Assert.Equal(SealState.Signed, seal.State);
        Assert.Equal("mof22", seal.Author);
        Assert.Equal(me.Fingerprint, seal.Fingerprint);
    }

    [Fact]
    public void AProtectedPackDoesNotOpenAsAZip()
    {
        // The whole of what the scrambling is for: renaming it and double-clicking gets nowhere.
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        Assert.Throws<InvalidDataException>(() => ZipFile.OpenRead(path));
        Assert.NotEqual(Zip(), File.ReadAllBytes(path));
    }

    [Fact]
    public void ChangingWhatIsInsideShowsUp()
    {
        // One byte or all of them: the signature covers the whole payload, so there is nothing to
        // be gained by testing a larger edit — and a test that reached in at a known offset would
        // be keeping its own copy of the file layout, which is the thing most likely to be wrong.
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        // One byte, in the payload rather than the header.
        var raw = File.ReadAllBytes(path);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(path, raw);

        Assert.Equal(SealState.Altered, PackFile.Inspect(path).State);
    }


    [Fact]
    public void ChangingTheNameOnItShowsUpAsWell()
    {
        // The name is part of what is signed, so it cannot be moved without the key: not onto
        // somebody else's work, and not off work that was theirs.
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        // Found in the header rather than at an offset. The header is readable on purpose, which
        // is all this needs to know about the container — and the replacement is the same length,
        // so nothing else moves either.
        var raw = File.ReadAllBytes(path);
        "mof99"u8.CopyTo(raw.AsSpan(raw.AsSpan().IndexOf("mof22"u8)));
        File.WriteAllBytes(path, raw);

        var seal = PackFile.Inspect(path);
        Assert.Equal("mof99", seal.Author);
        Assert.Equal(SealState.Altered, seal.State);
    }

    [Fact]
    public void SigningWithAnotherKeyReadsAsSomebodyElsesPack()
    {
        // Re-signing is not prevented — it cannot be. What it costs is the author's name: an
        // altered pack signed again carries a different fingerprint, and anybody who knows the
        // author's own can see that.
        using var me = PackAuthor.Mine(_home);
        var mineFingerprint = me.Fingerprint;

        var elsewhere = Directory.CreateTempSubdirectory("pgassettool-other").FullName;
        try
        {
            using var them = PackAuthor.Mine(elsewhere);
            var path = Path("resigned.pgmod");
            PackFile.Write(path, Zip(), them, "mof22");

            var seal = PackFile.Inspect(path);
            Assert.Equal(SealState.Signed, seal.State);
            Assert.NotEqual(mineFingerprint, seal.Fingerprint);
        }
        finally { Directory.Delete(elsewhere, recursive: true); }
    }

    [Fact]
    public void TheKeyIsMadeOnceAndKeptBesideEverythingElse()
    {
        using var first = PackAuthor.Mine(_home);
        using var again = PackAuthor.Mine(_home);

        Assert.True(File.Exists(Path(PackAuthor.FileName)));
        Assert.Equal(first.Fingerprint, again.Fingerprint);
    }

    [Fact]
    public void AKeyFileThatCannotBeReadIsReplacedRatherThanFatal()
    {
        // Losing the key costs an author their identity on later packs, which is bad. Refusing to
        // build anything at all would be worse.
        File.WriteAllText(Path(PackAuthor.FileName), "not a key");

        using var made = PackAuthor.Mine(_home);

        Assert.Equal(16, made.Fingerprint.Length);
        Assert.True(File.Exists(Path(PackAuthor.FileName) + ".unreadable"));
    }

    [Fact]
    public void SomethingThatIsNotAPackAtAllIsNotClaimedToBeSigned()
        => Assert.Equal(SealState.Unsigned, PackFile.Inspect(Path("nothing here.pgmod")).State);

}
