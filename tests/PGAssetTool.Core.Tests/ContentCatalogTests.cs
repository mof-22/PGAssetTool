using System.Text;
using System.Text.Json;
using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// Reading an Addressables catalog: the bundles it describes, and which of them an address needs.
///
/// The catalogs here are built byte by byte rather than taken from the game, both because a test
/// must not need an installation and because the layout is the thing under test. Every length and
/// order below was read off the game's own catalog: a key or a standalone string carries a
/// four-byte length in bytes, while the type names in front of an object's JSON carry a single-byte
/// one, and the JSON itself is UTF-16.
public class ContentCatalogTests
{
    private const string RemoteHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LocalHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ABundlesOptionsAreReadThroughTheLocationThatCarriesThem()
    {
        var catalog = Written();

        Assert.Equal(2, catalog.Bundles.Count);

        var remote = catalog.ByHash(RemoteHash);
        Assert.NotNull(remote);
        Assert.Equal("cache_one", remote.CacheName);
        Assert.Equal(4567u, remote.Crc);
        Assert.Equal(89_012, remote.Size);
        Assert.True(remote.IsRemote);
        Assert.Equal($"bundle_one_{RemoteHash}.bundle", remote.FileName);
    }

    /// Where a bundle is kept follows from what the catalog says, and the cache gives no names of
    /// its own: a downloaded bundle is &lt;cache name&gt;/&lt;hash&gt;/__data and a shipped one is
    /// its file name under the install.
    [Fact]
    public void WhereABundleIsKeptComesFromTheCatalog()
    {
        var catalog = Written();

        Assert.Equal(
            Path.Combine("C:", "cache", "cache_one", RemoteHash, "__data"),
            catalog.ByHash(RemoteHash)!.PathUnder(Path.Combine("C:", "cache")));
        Assert.Equal(
            Path.Combine("C:", "aa", $"bundle_two_{LocalHash}.bundle"),
            catalog.ByHash(LocalHash)!.PathIn(Path.Combine("C:", "aa")));
    }

    /// A bundle whose internal id is a url is one the game downloads; one addressed through the
    /// runtime path ships with the install. Nothing else tells the two apart.
    [Fact]
    public void AUrlMeansTheGameFetchesIt()
    {
        var catalog = Written();

        Assert.True(catalog.ByHash(RemoteHash)!.IsRemote);
        Assert.False(catalog.ByHash(LocalHash)!.IsRemote);
    }

    /// A location does not list its dependencies; it names the key they are filed under. So
    /// resolving an address is two hops, and both have to be made or an item answers with nothing.
    [Fact]
    public void AnAddressResolvesToItsBundlesThroughTheDependencyKey()
    {
        var bundles = Written().BundlesFor("Weapons/Thing");

        Assert.Equal(
            [$"bundle_one_{RemoteHash}.bundle", $"bundle_two_{LocalHash}.bundle"],
            bundles.Select(b => b.FileName));
    }

    [Fact]
    public void AnAddressTheCatalogDoesNotHoldAnswersWithNothing()
    {
        var catalog = Written();

        Assert.False(catalog.Knows("Weapons/NoSuchThing"));
        Assert.Empty(catalog.BundlesFor("Weapons/NoSuchThing"));
    }

    [Fact]
    public void ByCacheNameFindsTheSameBundle()
    {
        var catalog = Written();

        Assert.Equal(RemoteHash, catalog.ByCacheName("cache_one")!.Hash);
    }

    /// A catalog the tool cannot read is one kind of trouble, said the way every other unreadable
    /// game file is said — the file is the game's and verifying it through the store puts it back.
    [Fact]
    public void ACatalogThatIsNotJsonIsOneKindOfTrouble()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgat-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var trouble = Assert.Throws<InvalidDataException>(() => ContentCatalog.Read(path));
            Assert.Contains("not readable", trouble.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BlobsThatAreNotBase64AreOneKindOfTroubleToo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgat-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"m_KeyDataString":"not base64 at all"}""");
        try
        {
            Assert.Throws<InvalidDataException>(() => ContentCatalog.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// A catalog holding two bundles and one asset that needs both.
    private static ContentCatalog Written()
    {
        string[] internalIds =
        [
            $"https://cdn.test/addressables/6000/windows/1.0/bundle_one_{RemoteHash}.bundle",
            @"{UnityEngine.AddressableAssets.Addressables.RuntimePath}\StandaloneWindows64\"
                + $"bundle_two_{LocalHash}.bundle",
            "Assets/Test/Content/Thing.prefab",
        ];

        var keys = new MemoryStream();
        var addressAt = AsciiKey(keys, "Weapons/Thing");
        var dependencyAt = AsciiKey(keys, "1059593570");

        var extra = new MemoryStream();
        var oneAt = Options(extra, RemoteHash, crc: 4567, cache: "cache_one", size: 89_012);
        var twoAt = Options(extra, LocalHash, crc: 7654, cache: "cache_two", size: 210_98);

        // Two bundles, then the asset whose dependencies are filed under the second key. The asset
        // carries no object of its own, which is what -1 means.
        var entries = Entries(
            [
                new(0, 0, -1, 0, oneAt, 0, 0),
                new(1, 0, -1, 0, twoAt, 1, 0),
                new(2, 1, 1, 0, -1, 2, 1),
            ]);

        // Bucket 0 is the address and holds the asset; bucket 1 is the key its dependencies are
        // under and holds the two bundles.
        var buckets = Buckets([(addressAt, [2]), (dependencyAt, [0, 1])]);

        var catalog = new Dictionary<string, object?>
        {
            ["m_LocatorId"] = "AddressablesMainContentCatalog",
            ["m_InternalIds"] = internalIds,
            ["m_ProviderIds"] = new[] { "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider" },
            ["m_KeyDataString"] = Convert.ToBase64String(keys.ToArray()),
            ["m_BucketDataString"] = Convert.ToBase64String(buckets),
            ["m_EntryDataString"] = Convert.ToBase64String(entries),
            ["m_ExtraDataString"] = Convert.ToBase64String(extra.ToArray()),
        };

        var path = Path.Combine(Path.GetTempPath(), $"pgat-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(catalog));
        try
        {
            return ContentCatalog.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// Writes a key and answers the offset it went to, which is what a bucket holds.
    private static int AsciiKey(MemoryStream keys, string text)
    {
        var at = (int)keys.Position;
        keys.WriteByte(0);
        var bytes = Encoding.ASCII.GetBytes(text);
        keys.Write(BitConverter.GetBytes(bytes.Length));
        keys.Write(bytes);
        return at;
    }

    /// Writes one bundle's request options and answers the offset, which is what an entry holds.
    private static int Options(MemoryStream extra, string hash, uint crc, string cache, long size)
    {
        var at = (int)extra.Position;
        extra.WriteByte(7);
        foreach (var name in new[]
                 {
                     "Unity.ResourceManager, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null",
                     "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions",
                 })
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            extra.WriteByte((byte)bytes.Length);
            extra.Write(bytes);
        }

        var json = Encoding.Unicode.GetBytes(
            $$"""{"m_Hash":"{{hash}}","m_Crc":{{crc}},"m_BundleName":"{{cache}}","m_BundleSize":{{size}},"m_UseCrcForCachedBundles":true}""");
        extra.Write(BitConverter.GetBytes(json.Length));
        extra.Write(json);
        return at;
    }

    private readonly record struct Row(
        int InternalId, int Provider, int DependencyKey, int DependencyHash,
        int DataIndex, int PrimaryKey, int ResourceType);

    private static byte[] Entries(Row[] rows)
    {
        var data = new MemoryStream();
        data.Write(BitConverter.GetBytes(rows.Length));
        foreach (var r in rows)
            foreach (var number in new[]
                     {
                         r.InternalId, r.Provider, r.DependencyKey, r.DependencyHash,
                         r.DataIndex, r.PrimaryKey, r.ResourceType,
                     })
                data.Write(BitConverter.GetBytes(number));
        return data.ToArray();
    }

    private static byte[] Buckets((int KeyOffset, int[] Entries)[] buckets)
    {
        var data = new MemoryStream();
        data.Write(BitConverter.GetBytes(buckets.Length));
        foreach (var (keyOffset, entries) in buckets)
        {
            data.Write(BitConverter.GetBytes(keyOffset));
            data.Write(BitConverter.GetBytes(entries.Length));
            foreach (var entry in entries) data.Write(BitConverter.GetBytes(entry));
        }
        return data.ToArray();
    }
}
