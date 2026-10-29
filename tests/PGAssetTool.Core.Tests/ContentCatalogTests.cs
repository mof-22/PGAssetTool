using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// Reading an Addressables catalog: the bundles it describes, and which of them an address needs.
/// The catalogs are built in `CatalogFixture`, byte by byte, because the layout is the thing under
/// test and a test must not need an installation.
public class ContentCatalogTests
{
    [Fact]
    public void ABundlesOptionsAreReadThroughTheLocationThatCarriesThem()
    {
        var catalog = CatalogFixture.Written();

        Assert.Equal(2, catalog.Bundles.Count);

        var remote = catalog.ByHash(CatalogFixture.RemoteHash);
        Assert.NotNull(remote);
        Assert.Equal("cache_one", remote.CacheName);
        Assert.Equal(4567u, remote.Crc);
        Assert.Equal(89_012, remote.Size);
        Assert.Equal(CatalogFixture.RemoteFileName, remote.FileName);
    }

    /// Where a bundle is kept follows from what the catalog says, and the cache gives no names of
    /// its own: a downloaded bundle is &lt;cache name&gt;/&lt;hash&gt;/__data and a shipped one is
    /// its file name under the install.
    [Fact]
    public void WhereABundleIsKeptComesFromTheCatalog()
    {
        var catalog = CatalogFixture.Written();

        Assert.Equal(
            Path.Combine("C:", "cache", "cache_one", CatalogFixture.RemoteHash, "__data"),
            catalog.ByHash(CatalogFixture.RemoteHash)!.PathUnder(Path.Combine("C:", "cache")));
        Assert.Equal(
            Path.Combine("C:", "aa", CatalogFixture.LocalFileName),
            catalog.ByHash(CatalogFixture.LocalHash)!.PathIn(Path.Combine("C:", "aa")));
    }

    /// A bundle whose internal id is a url is one the game downloads; one addressed through the
    /// runtime path ships with the install. Nothing else tells the two apart, and the difference
    /// decides which of the two places it is looked for in.
    [Fact]
    public void AUrlMeansTheGameFetchesIt()
    {
        var catalog = CatalogFixture.Written();

        Assert.True(catalog.ByHash(CatalogFixture.RemoteHash)!.IsRemote);
        Assert.False(catalog.ByHash(CatalogFixture.LocalHash)!.IsRemote);
    }

    /// A location does not list its dependencies; it names the key they are filed under. So
    /// resolving an address is two hops, and both have to be made or an item answers with nothing.
    [Fact]
    public void AnAddressResolvesToItsBundlesThroughTheDependencyKey()
    {
        var bundles = CatalogFixture.Written().BundlesFor(CatalogFixture.Address);

        Assert.Equal(
            [CatalogFixture.RemoteFileName, CatalogFixture.LocalFileName],
            bundles.Select(b => b.FileName));
    }

    /// A location also carries the asset path the bundle is asked for, which is what the bundle's
    /// own container table names it by.
    [Fact]
    public void ALocationCarriesTheAssetPathAndItsProvider()
    {
        var location = Assert.Single(CatalogFixture.Written().LocationsFor(CatalogFixture.Address));

        Assert.Equal(CatalogFixture.AssetPath, location.InternalId);
        Assert.EndsWith("BundledAssetProvider", location.Provider);
        Assert.Equal(2, location.Bundles.Count);
    }

    [Fact]
    public void AnAddressTheCatalogDoesNotHoldAnswersWithNothing()
    {
        var catalog = CatalogFixture.Written();

        Assert.False(catalog.Knows("Weapons/NoSuchThing"));
        Assert.Empty(catalog.BundlesFor("Weapons/NoSuchThing"));
        Assert.Empty(catalog.LocationsFor("Weapons/NoSuchThing"));
    }

    [Fact]
    public void ByCacheNameFindsTheSameBundle()
        => Assert.Equal(
            CatalogFixture.RemoteHash,
            CatalogFixture.Written().ByCacheName("cache_one")!.Hash);

    /// A catalog the tool cannot read is one kind of trouble, said the way every other unreadable
    /// game file is said — the file is the game's, and verifying it through the store puts it back.
    [Fact]
    public void ACatalogThatIsNotJsonIsOneKindOfTrouble()
    {
        var trouble = Assert.Throws<InvalidDataException>(() => CatalogFixture.ReadBack("{ not json"));
        Assert.Contains("not readable", trouble.Message);
    }

    [Fact]
    public void BlobsThatAreNotBase64AreOneKindOfTroubleToo()
        => Assert.Throws<InvalidDataException>(
            () => CatalogFixture.ReadBack("""{"m_KeyDataString":"not base64 at all"}"""));
}
