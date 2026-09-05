using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Tests;

public class ClassPackageTests
{
    [Fact]
    public void TheClassDatabaseTravelsInsideTheAssembly()
    {
        // A single-file build is one executable with nowhere to keep the database beside it, and
        // without it the game's own .assets files cannot be read at all — which is where the newest
        // weapons keep their icons.
        using var embedded = typeof(ClassPackage).Assembly.GetManifestResourceStream(ClassPackage.FileName);

        Assert.NotNull(embedded);
        Assert.True(embedded.Length > 100_000, $"the embedded database is only {embedded.Length} bytes");
    }

    [Fact]
    public void LocateProducesAReadableFileWithNothingBesideTheAssembly()
    {
        var path = ClassPackage.Locate();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"'{path}' does not exist");

        using var embedded = typeof(ClassPackage).Assembly.GetManifestResourceStream(ClassPackage.FileName)!;
        Assert.Equal(embedded.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void UnpackingTwiceReusesTheFileRatherThanRewritingIt()
    {
        var first = ClassPackage.Locate()!;
        var written = new FileInfo(first).LastWriteTimeUtc;

        Assert.Equal(first, ClassPackage.Locate());
        Assert.Equal(written, new FileInfo(first).LastWriteTimeUtc);
    }
}
