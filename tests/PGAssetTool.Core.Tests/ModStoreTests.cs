using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Tests;

public class ModStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-store").FullName;
    private readonly ModStore _store;

    private readonly string _home;

    public ModStoreTests()
    {
        var bundles = Path.Combine(_root, "Game_Data", "StreamingAssets", "Cache", "bundles");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, "embedded_asset_bundles.json"), "[]");
        _home = Path.Combine(_root, "home");
        _store = new ModStore(GameInstallation.Open(_root), _home);
    }

    [Fact]
    public void TheStoreLivesUnderItsHome()
    {
        // Not under the game: uninstalling it would take the backups with it, at the moment the
        // downloaded bundle cache outlives them.
        Assert.StartsWith(_home, _store.Root, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(_root) + Path.DirectorySeparatorChar + "Game_Data", _store.Root);
    }

    [Fact]
    public void TwoInstallationsDoNotShareAStore()
    {
        var other = Directory.CreateTempSubdirectory("pgassettool-other").FullName;
        try
        {
            var bundles = Path.Combine(other, "Game_Data", "StreamingAssets", "Cache", "bundles");
            Directory.CreateDirectory(bundles);
            File.WriteAllText(Path.Combine(bundles, "embedded_asset_bundles.json"), "[]");
            Assert.NotEqual(_store.Root, new ModStore(GameInstallation.Open(other), _home).Root);
        }
        finally { Directory.Delete(other, recursive: true); }
    }

    [Fact]
    public void TheDefaultHomeSitsBesideTheToolRatherThanInsideItsBuildOutput()
    {
        var home = ModStore.DefaultHome();
        Assert.EndsWith(ModStore.DataDirectoryName, home, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", home);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteLiveBundle(string bundle, string hash, string content)
    {
        var path = Path.Combine(_root, "Game_Data", "StreamingAssets", "Cache", "bundles", bundle, hash, bundle);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void BackupAndRestoreReturnTheOriginalBytes()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);
        File.WriteAllText(live, "modified");

        Assert.True(_store.RestoreIfBackedUp(CacheKind.Shipped, "woi_0", "aaa", live));
        Assert.Equal("original", File.ReadAllText(live));
    }

    [Fact]
    public void BackingUpTwiceKeepsTheFirstCopy()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);
        File.WriteAllText(live, "modified");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);

        _store.RestoreIfBackedUp(CacheKind.Shipped, "woi_0", "aaa", live);
        Assert.Equal("original", File.ReadAllText(live));
    }

    [Fact]
    public void RestoringWithoutABackupSaysSo()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        Assert.False(_store.RestoreIfBackedUp(CacheKind.Shipped, "woi_0", "bbb", live));
    }

    [Fact]
    public void BackupsAreFiledUnderTheBundleVersionTheyCameFrom()
    {
        var oldLive = WriteLiveBundle("woi_0", "aaa", "v1");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", oldLive);
        var newLive = WriteLiveBundle("woi_0", "bbb", "v2");
        _store.Backup(CacheKind.Shipped, "woi_0", "bbb", newLive);

        Assert.True(_store.HasBackup(CacheKind.Shipped, "woi_0", "aaa"));
        Assert.True(_store.HasBackup(CacheKind.Shipped, "woi_0", "bbb"));
    }

    [Fact]
    public void AnUpdateLeavesTheBackupForTheSupersededVersionToBePruned()
    {
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", WriteLiveBundle("woi_0", "aaa", "v1"));
        _store.Backup(CacheKind.Shipped, "woi_0", "bbb", WriteLiveBundle("woi_0", "bbb", "v2"));
        _store.Backup(CacheKind.Shipped, "bhlw", "ccc", WriteLiveBundle("bhlw", "ccc", "unchanged"));

        var pruned = _store.PruneStaleBackups(new Dictionary<string, string>
        {
            ["woi_0"] = "bbb",
            ["bhlw"] = "ccc",
        });

        Assert.Equal(["shipped/woi_0/aaa"], pruned);
        Assert.False(_store.HasBackup(CacheKind.Shipped, "woi_0", "aaa"));
        Assert.True(_store.HasBackup(CacheKind.Shipped, "woi_0", "bbb"));
        Assert.True(_store.HasBackup(CacheKind.Shipped, "bhlw", "ccc"));
    }

    [Fact]
    public void ABundleIsPristineWhileItsContentStillMatchesItsCacheDirectory()
    {
        // The game names each bundle's cache directory after the file's MD5, which is what makes
        // "has something else already edited this?" answerable at all.
        var path = Path.Combine(_root, "sample");
        File.WriteAllText(path, "shipped bytes");
        var hash = BundleIntegrity.Md5(path);

        Assert.True(BundleIntegrity.IsPristine(path, hash));
        Assert.True(BundleIntegrity.IsPristine(path, hash.ToUpperInvariant()));

        File.AppendAllText(path, "!");
        Assert.False(BundleIntegrity.IsPristine(path, hash));
    }

    [Fact]
    public void WritingKeepsTheStateItReplaced()
    {
        // Losing this file loses the record of which mod put what where, and with it the ability to
        // uninstall through the tool.
        var mod = new InstalledMod
        {
            Id = "a", Name = "A", PackPath = "a.pgmod", InstalledAt = DateTimeOffset.UnixEpoch,
            GameVersion = "26.11.0", TouchedBundles = new Dictionary<string, string>(),
        };
        _store.Write([mod]);
        _store.Write([]);

        Assert.Empty(_store.Read());
        Assert.Contains("\"a\"", File.ReadAllText(Path.Combine(_store.Root, "installed.json.previous")));
    }

    [Fact]
    public void InstalledModsRoundTrip()
    {
        _store.Write([
            new InstalledMod
            {
                Id = "a", Name = "A", PackPath = "a.pgmod", InstalledAt = DateTimeOffset.UnixEpoch,
                GameVersion = "26.11.0", Enabled = false,
                TouchedBundles = new Dictionary<string, string> { ["woi_0"] = "aaa" },
            },
        ]);

        var mod = Assert.Single(_store.Read());
        Assert.Equal("a", mod.Id);
        Assert.False(mod.Enabled);
        Assert.Equal("aaa", mod.TouchedBundles["woi_0"]);
    }

    [Fact]
    public void EveryBackupIsFoundEvenWhenNoModClaimsItAnyMore()
    {
        // What has been written to is recorded by the backups, not by the installed list: uninstall
        // drops the mod before the reconcile that would put its bundles back.
        _store.Backup(CacheKind.Shipped, "bhlw", "b356", Bundle("original"));
        _store.Backup(CacheKind.Downloaded, "woi_0", "1773", Bundle("original"));

        var found = _store.BackedUp().OrderBy(b => b.Bundle).ToList();

        Assert.Equal([(CacheKind.Shipped, "bhlw", "b356"), (CacheKind.Downloaded, "woi_0", "1773")],
            found.OrderBy(b => b.Bundle == "bhlw" ? 0 : 1));
    }

    [Fact]
    public void TheLayoutThatPredatedCacheKindsIsIgnoredRatherThanMisread()
    {
        // Backups once sat directly under the bundle name, with no cache segment above it.
        var legacy = Path.Combine(_store.BackupRoot, "woi_0", "1773");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "woi_0"), "original");

        Assert.Empty(_store.BackedUp());
    }

    private string Bundle(string contents)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("n"));
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void AnInstalledPackIsCopiedOutOfWhereverItWasBuilt()
    {
        // A pack built into a workspace is one deletion away from leaving an installed mod with no
        // file to reapply or remove from. That happened, and cost an uninstall to recover.
        var built = Path.Combine(_root, "somewhere", "weapon.pgmod");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllText(built, "a pack");

        var kept = _store.Keep(built);

        Assert.StartsWith(_store.ModsDirectory, kept, StringComparison.Ordinal);
        Assert.Equal("a pack", File.ReadAllText(kept));

        Directory.Delete(Path.GetDirectoryName(built)!, recursive: true);
        Assert.True(File.Exists(kept), "the copy did not outlive the directory it came from");
    }

    [Fact]
    public void TwoPacksOfTheSameNameFromDifferentPlacesDoNotCollide()
    {
        var first = Path.Combine(_root, "a", "weapon.pgmod");
        var second = Path.Combine(_root, "b", "weapon.pgmod");
        foreach (var path in new[] { first, second })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, path);
        }

        Assert.NotEqual(_store.Keep(first), _store.Keep(second));
        Assert.Equal(first, File.ReadAllText(_store.Keep(first)));
        Assert.Equal(second, File.ReadAllText(_store.Keep(second)));
    }

    [Fact]
    public void KeepingAPackAlreadyInTheStoreLeavesItAlone()
    {
        // Reinstalling from the copy must not spiral into copies of copies.
        var built = Path.Combine(_root, "weapon.pgmod");
        File.WriteAllText(built, "a pack");

        var kept = _store.Keep(built);
        Assert.Equal(kept, _store.Keep(kept));
        Assert.Single(Directory.GetFiles(_store.ModsDirectory));
    }

    [Fact]
    public void ThePackStoreSitsBesideEverythingElseTheToolKeeps()
        => Assert.Equal(Path.Combine(_home, "mods"), _store.ModsDirectory);

    [Fact]
    public void TheDigestGoesAfterTheNameSoTheFolderSortsByWhatItHolds()
    {
        var built = Path.Combine(_root, "somewhere", "0016_Beretta.pgmod");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllText(built, "a pack");

        var kept = Path.GetFileName(_store.Keep(built));

        Assert.StartsWith("0016_Beretta-", kept, StringComparison.Ordinal);
        Assert.EndsWith(".pgmod", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void PacksFiledUnderTheOldNameAreMovedAndTheLedgerFollowsThem()
    {
        // Renaming without the second half would leave the mod naming a file that is not there,
        // and with it no way to reapply or cleanly remove it.
        Directory.CreateDirectory(_store.ModsDirectory);
        var old = Path.Combine(_store.ModsDirectory, "085cc273-0016_Beretta.pgmod");
        File.WriteAllText(old, "a pack");
        _store.Write([Installed("beretta", old)]);

        var moved = _store.TidyKeptPackNames();

        Assert.Equal(["0016_Beretta-085cc273.pgmod"], moved);
        Assert.False(File.Exists(old));
        Assert.Equal(
            Path.Combine(_store.ModsDirectory, "0016_Beretta-085cc273.pgmod"),
            _store.Read().Single().PackPath);
    }

    [Fact]
    public void APackAlreadyNamedTheNewWayIsNotTakenForAnOldOne()
    {
        // "0016_Ber" is eight characters followed by a dash, which is the shape being looked for —
        // but not hex, so it is a name rather than a digest.
        Directory.CreateDirectory(_store.ModsDirectory);
        foreach (var name in new[] { "0016_Beretta-085cc273.pgmod", "0016_Ber-etta.pgmod" })
            File.WriteAllText(Path.Combine(_store.ModsDirectory, name), "a pack");

        Assert.Empty(_store.TidyKeptPackNames());
        Assert.Equal(2, Directory.GetFiles(_store.ModsDirectory).Length);
    }

    private static InstalledMod Installed(string id, string packPath) => new()
    {
        Id = id,
        Name = id,
        PackPath = packPath,
        InstalledAt = DateTimeOffset.UnixEpoch,
        GameVersion = "1.0",
        TouchedBundles = new Dictionary<string, string>(),
    };
}
