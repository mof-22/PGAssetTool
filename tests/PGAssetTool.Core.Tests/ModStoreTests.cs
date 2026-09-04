using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Tests;

public class ModStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-store").FullName;
    private readonly ModStore _store;

    public ModStoreTests()
    {
        var bundles = Path.Combine(_root, "Game_Data", "StreamingAssets", "Cache", "bundles");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, "embedded_asset_bundles.json"), "[]");
        _store = new ModStore(GameInstallation.Open(_root));
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
        _store.Backup("woi_0", "aaa", live);
        File.WriteAllText(live, "modified");

        Assert.True(_store.RestoreIfBackedUp("woi_0", "aaa", live));
        Assert.Equal("original", File.ReadAllText(live));
    }

    [Fact]
    public void BackingUpTwiceKeepsTheFirstCopy()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        _store.Backup("woi_0", "aaa", live);
        File.WriteAllText(live, "modified");
        _store.Backup("woi_0", "aaa", live);

        _store.RestoreIfBackedUp("woi_0", "aaa", live);
        Assert.Equal("original", File.ReadAllText(live));
    }

    [Fact]
    public void RestoringWithoutABackupSaysSo()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        Assert.False(_store.RestoreIfBackedUp("woi_0", "bbb", live));
    }

    [Fact]
    public void BackupsAreFiledUnderTheBundleVersionTheyCameFrom()
    {
        var oldLive = WriteLiveBundle("woi_0", "aaa", "v1");
        _store.Backup("woi_0", "aaa", oldLive);
        var newLive = WriteLiveBundle("woi_0", "bbb", "v2");
        _store.Backup("woi_0", "bbb", newLive);

        Assert.True(_store.HasBackup("woi_0", "aaa"));
        Assert.True(_store.HasBackup("woi_0", "bbb"));
    }

    [Fact]
    public void AnUpdateLeavesTheBackupForTheSupersededVersionToBePruned()
    {
        _store.Backup("woi_0", "aaa", WriteLiveBundle("woi_0", "aaa", "v1"));
        _store.Backup("woi_0", "bbb", WriteLiveBundle("woi_0", "bbb", "v2"));
        _store.Backup("bhlw", "ccc", WriteLiveBundle("bhlw", "ccc", "unchanged"));

        var pruned = _store.PruneStaleBackups(new Dictionary<string, string>
        {
            ["woi_0"] = "bbb",
            ["bhlw"] = "ccc",
        });

        Assert.Equal(["woi_0/aaa"], pruned);
        Assert.False(_store.HasBackup("woi_0", "aaa"));
        Assert.True(_store.HasBackup("woi_0", "bbb"));
        Assert.True(_store.HasBackup("bhlw", "ccc"));
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
}
