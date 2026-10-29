using System.Text;
using System.Text.Json;
using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// Builds an Addressables catalog byte by byte, so nothing here needs an installation.
///
/// Every length and order below was read off the game's own catalog: a key or a standalone string
/// carries a four-byte length in bytes, while the type names in front of an object's JSON carry a
/// single-byte one, and the JSON itself is UTF-16.
internal static class CatalogFixture
{
    internal const string RemoteHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string LocalHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string Address = "Weapons/Thing";
    internal const string AssetPath = "Assets/Test/Content/Thing.prefab";

    internal static string RemoteFileName => $"bundle_one_{RemoteHash}.bundle";
    internal static string LocalFileName => $"bundle_two_{LocalHash}.bundle";

    /// A catalog holding two bundles and one asset that needs both. The written file is deleted
    /// before this returns; the catalog is read whole.
    internal static ContentCatalog Written()
    {
        string[] internalIds =
        [
            $"https://cdn.test/addressables/6000/windows/1.0/{RemoteFileName}",
            @"{UnityEngine.AddressableAssets.Addressables.RuntimePath}\StandaloneWindows64\"
                + LocalFileName,
            AssetPath,
        ];

        var keys = new MemoryStream();
        var addressAt = AsciiKey(keys, Address);
        var dependencyAt = AsciiKey(keys, "1059593570");

        var extra = new MemoryStream();
        var oneAt = Options(extra, RemoteHash, crc: 4567, cache: "cache_one", size: 89_012);
        var twoAt = Options(extra, LocalHash, crc: 7654, cache: "cache_two", size: 21_098);

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
            ["m_ProviderIds"] = new[]
            {
                "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider",
                "UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider",
            },
            ["m_KeyDataString"] = Convert.ToBase64String(keys.ToArray()),
            ["m_BucketDataString"] = Convert.ToBase64String(buckets),
            ["m_EntryDataString"] = Convert.ToBase64String(entries),
            ["m_ExtraDataString"] = Convert.ToBase64String(extra.ToArray()),
        };

        return ReadBack(JsonSerializer.Serialize(catalog));
    }

    internal static ContentCatalog ReadBack(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgat-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
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

    internal readonly record struct Row(
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
