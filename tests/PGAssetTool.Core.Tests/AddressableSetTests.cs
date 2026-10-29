using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// Resolving a bundle to a file on an installation where almost nothing has been downloaded, which
/// is the normal state: the game fetches a weapon or a map when the player picks it, and 1,088 of
/// the 1,530 weapons were waiting on a download on the machine this was written against.
///
/// Opening a bundle needs a real one, which no test carries; what is checked here is everything
/// around that — where a bundle is looked for, and what is said about one that is not there.
public class AddressableSetTests
{
    /// A remote bundle is only looked for in the cache and a local one only in the install. Falling
    /// back from one to the other would answer "found" for a bundle the player has never fetched.
    [Fact]
    public void ARemoteBundleIsLookedForInTheCacheAndALocalOneInTheInstall()
    {
        using var empty = Scratch();
        var bundles = Bundles(empty.Path);

        var remote = bundles.Catalog.ByHash(CatalogFixture.RemoteHash)!;
        var local = bundles.Catalog.ByHash(CatalogFixture.LocalHash)!;

        Assert.Null(bundles.Resolve(remote));
        Assert.Null(bundles.Resolve(local));

        // the local one, put where the install keeps them
        var localDirectory = ContentCatalog.LocalBundlesIn(empty.Path);
        Directory.CreateDirectory(localDirectory);
        File.WriteAllBytes(Path.Combine(localDirectory, CatalogFixture.LocalFileName), [1, 2, 3]);

        Assert.Null(bundles.Resolve(remote));
        var found = bundles.Resolve(local);
        Assert.NotNull(found);
        Assert.False(found.IsCached);
    }

    [Fact]
    public void ACachedBundleIsFoundUnderItsCacheNameAndHash()
    {
        using var scratch = Scratch();
        var cacheRoot = Path.Combine(scratch.Path, "cache");
        var bundles = Bundles(scratch.Path, AddressablesCache.Open(cacheRoot));

        var remote = bundles.Catalog.ByHash(CatalogFixture.RemoteHash)!;
        Directory.CreateDirectory(Path.Combine(cacheRoot, remote.CacheName, remote.Hash));
        File.WriteAllBytes(remote.PathUnder(cacheRoot), [1, 2, 3]);

        var found = bundles.Resolve(remote);
        Assert.NotNull(found);
        Assert.True(found.IsCached);
        Assert.Equal(remote.PathUnder(cacheRoot), found.Path);
    }

    /// An item whose bundles have not been fetched is the common case, and it has to be told apart
    /// from one the tool failed to read: `Missing` names them rather than answering nothing.
    [Fact]
    public void BundlesThatHaveNotBeenFetchedAreNamed()
    {
        using var empty = Scratch();
        using var set = new AddressableSet(Bundles(empty.Path));

        Assert.Equal(
            [CatalogFixture.RemoteFileName, CatalogFixture.LocalFileName],
            set.Missing(CatalogFixture.Address));
        Assert.Empty(set.Find(CatalogFixture.Address));
    }

    [Fact]
    public void AnAddressTheCatalogDoesNotHoldHasNothingMissingEither()
    {
        using var empty = Scratch();
        using var set = new AddressableSet(Bundles(empty.Path));

        Assert.Empty(set.Missing("Weapons/NoSuchThing"));
        Assert.Empty(set.Find("Weapons/NoSuchThing"));
    }

    /// Asked for the path of a bundle that is not there, the reader says which of the two reasons
    /// it is: a download that has not happened, or a file the installation should have.
    [Fact]
    public void TheReasonABundleIsNotThereIsSaid()
    {
        using var empty = Scratch();
        using var set = new AddressableSet(Bundles(empty.Path));
        var catalog = set.Catalog;

        var remote = Assert.Throws<FileNotFoundException>(
            () => set.PathOf(catalog.ByHash(CatalogFixture.RemoteHash)!));
        Assert.Contains("has not been downloaded", remote.Message);

        var local = Assert.Throws<FileNotFoundException>(
            () => set.PathOf(catalog.ByHash(CatalogFixture.LocalHash)!));
        Assert.Contains("not in the installation", local.Message);
    }

    /// A kept original is read in place of what the game would load, which is how an extract reads
    /// the game as it was delivered rather than as a mod left it.
    [Fact]
    public void AKeptOriginalIsReadInsteadOfWhatTheGameWouldLoad()
    {
        using var scratch = Scratch();
        var localDirectory = ContentCatalog.LocalBundlesIn(scratch.Path);
        Directory.CreateDirectory(localDirectory);
        var live = Path.Combine(localDirectory, CatalogFixture.LocalFileName);
        File.WriteAllBytes(live, [9, 9, 9]);

        var kept = Path.Combine(scratch.Path, "original.bundle");
        File.WriteAllBytes(kept, [1, 2, 3]);

        using var set = new AddressableSet(
            Bundles(scratch.Path),
            originals: bundle => bundle.Hash == CatalogFixture.LocalHash ? kept : null);

        Assert.Equal(kept, set.PathOf(set.Catalog.ByHash(CatalogFixture.LocalHash)!));
    }

    private static AddressableBundles Bundles(string dataDirectory, AddressablesCache? cache = null)
        => new(CatalogFixture.Written(), dataDirectory, cache);

    private static Scratchpad Scratch() => new();

    private sealed class Scratchpad : IDisposable
    {
        public Scratchpad()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"pgat-addressables-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
