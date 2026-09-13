using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Tests;

public class GameUpdateTests
{
    private static InstalledMod Mod(string id, bool enabled, string version, params (string Bundle, string Hash)[] touched)
        => new()
        {
            Id = id,
            Name = id,
            PackPath = id + ".pgmod",
            InstalledAt = DateTimeOffset.UnixEpoch,
            GameVersion = version,
            Enabled = enabled,
            TouchedBundles = touched.ToDictionary(t => t.Bundle, t => t.Hash),
        };

    private static readonly BundleEntry[] Manifest = [new("dw", "aaaa"), new("bw", "bbbb"), new("ecw_6", "cccc")];

    [Fact]
    public void AModWrittenIntoTheBundlesTheGameStillLoadsIsNotStale()
        => Assert.Empty(GameUpdate.Stale([Mod("a", true, "26.11.0", ("dw", "aaaa"), ("bw", "bbbb"))], Manifest));

    [Fact]
    public void ABundleWhoseHashMovedMakesItsModStale()
    {
        // What an update does: the bundle is replaced under a new hash, and the game loads that one.
        var stale = GameUpdate.Stale([Mod("a", true, "26.10.2", ("dw", "aaaa"), ("bw", "old"))], Manifest);

        var only = Assert.Single(stale);
        Assert.Equal("a", only.Mod.Id);
        Assert.Equal(["bw"], only.Bundles);
    }

    [Fact]
    public void ABundleTheGameNoLongerHasMakesItsModStale()
        => Assert.Equal(["gone"],
            Assert.Single(GameUpdate.Stale([Mod("a", true, "26.10.2", ("gone", "dddd"))], Manifest)).Bundles);

    [Fact]
    public void AModThatIsOffIsNotAskedAbout()
    {
        // It is not in the game on purpose, so an update has taken nothing from it.
        Assert.Empty(GameUpdate.Stale([Mod("a", false, "26.10.2", ("bw", "old"))], Manifest));
    }

    [Fact]
    public void AModThatWroteNothingIsNotStale()
        => Assert.Empty(GameUpdate.Stale([Mod("a", true, "26.10.2")], Manifest));

    [Fact]
    public void HashesAreComparedWithoutRegardToCase()
        => Assert.Empty(GameUpdate.Stale([Mod("a", true, "26.11.0", ("DW", "AAAA"))], Manifest));

    [Fact]
    public void NothingStaleIsNothingToSay()
        => Assert.Null(GameUpdate.Notice([], "26.11.0"));

    [Fact]
    public void TheNoticeSaysWhichVersionsWhenTheModsAgreeOnOne()
    {
        var stale = GameUpdate.Stale(
            [Mod("a", true, "26.10.2", ("bw", "old")), Mod("b", true, "26.10.2", ("dw", "old"))], Manifest);

        var notice = GameUpdate.Notice(stale, "26.11.0");

        Assert.NotNull(notice);
        Assert.Contains("from 26.10.2 to 26.11.0", notice);
        Assert.Contains("2 of your mods", notice);
    }

    [Fact]
    public void TheNoticeLeavesTheOldVersionOutWhenTheModsDisagree()
    {
        var stale = GameUpdate.Stale(
            [Mod("a", true, "26.10.1", ("bw", "old")), Mod("b", true, "26.10.2", ("dw", "old"))], Manifest);

        var notice = GameUpdate.Notice(stale, "26.11.0")!;

        Assert.DoesNotContain(" from ", notice);
        Assert.Contains("to 26.11.0", notice);
    }
}
