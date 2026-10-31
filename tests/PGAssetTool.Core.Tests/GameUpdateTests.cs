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
    public void ABundleTheGameWillRefuseMakesItsModStale()
    {
        // What a game update does to the content route without moving a single hash: the catalog is
        // rewritten, the mark that turned the check off is gone, and the bundle still holds the mod.
        var stale = GameUpdate.Stale(
            [Mod("a", true, "26.15.1", ("dw", "aaaa"), ("bw", "bbbb"))],
            Manifest.ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase),
            bundle => bundle == "bw");

        var only = Assert.Single(stale);
        Assert.Empty(only.Replaced);
        Assert.Equal(["bw"], only.Rejected);
    }

    [Fact]
    public void ABundleThatHasMovedIsNotAlsoAskedWhetherItWillBeRefused()
    {
        // Its replacement is the game's own and passes its own check. Counting it twice would say
        // the player has lost the bundle when all they have lost is the mod.
        var stale = GameUpdate.Stale(
            [Mod("a", true, "26.15.1", ("bw", "old"))],
            Manifest.ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase),
            _ => true);

        var only = Assert.Single(stale);
        Assert.Equal(["bw"], only.Replaced);
        Assert.Empty(only.Rejected);
    }

    [Fact]
    public void TheNoticeSaysWhenABundleWillNotLoadAtAll()
    {
        var stale = GameUpdate.Stale(
            [Mod("a", true, "26.15.1", ("dw", "aaaa"), ("bw", "bbbb"))],
            Manifest.ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase),
            _ => true);

        var notice = GameUpdate.Notice(stale, "26.15.2")!;

        Assert.Contains("2 of the bundles will not load at all", notice);
    }

    [Fact]
    public void TheNoticeLeavesThatOutWhenEveryBundleStillLoads()
        => Assert.DoesNotContain("will not load at all",
            GameUpdate.Notice(GameUpdate.Stale([Mod("a", true, "26.10.2", ("bw", "old"))], Manifest), "26.11.0")!);

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
