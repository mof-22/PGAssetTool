using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGAssetTool.Core.Addressables;

/// One bundle as the catalog describes it.
///
/// <paramref name="CacheName"/> and <paramref name="Hash"/> are the two directories a downloaded
/// bundle is cached under; <paramref name="Crc"/> is what the game checks its contents against, and
/// a bundle whose bytes do not hash to it is thrown away and fetched again.
public sealed record CatalogBundle(
    string FileName,
    string CacheName,
    string Hash,
    uint Crc,
    long Size,
    bool IsRemote,
    string InternalId)
{
    /// Downloaded bundles live at &lt;cache&gt;/&lt;cache name&gt;/&lt;hash&gt;/__data. The file beside
    /// it holds no name, so this path is the only way from a name to the bytes.
    public string PathUnder(string cacheDirectory)
        => Path.Combine(cacheDirectory, CacheName, Hash, "__data");

    /// Shipped bundles sit under the install by file name instead.
    public string PathIn(string localDirectory) => Path.Combine(localDirectory, FileName);
}

/// The Addressables catalog: what the game can load, which bundle each thing is in, and what each
/// bundle has to hash to.
///
/// Since 26.15 this is where the game's content is. The old bundle manifest still names 90 bundles
/// and nothing an item is made of is among them.
public sealed class ContentCatalog
{
    private readonly CatalogTables.Entry[] _entries;
    private readonly CatalogTables.Bucket[] _buckets;
    private readonly byte[] _keys;
    private readonly string[] _internalIds;
    private readonly Dictionary<string, List<int>> _bucketsByKey;
    private readonly Dictionary<string, CatalogBundle> _byHash;
    private readonly Dictionary<string, CatalogBundle> _byCacheName;

    private ContentCatalog(
        string path, string locatorId, byte[] keys, byte[] buckets, byte[] entries, byte[] extra,
        string[] internalIds)
    {
        (FilePath, LocatorId) = (path, locatorId);
        _keys = keys;
        _internalIds = internalIds;
        _entries = CatalogTables.ReadEntries(entries);
        _buckets = CatalogTables.ReadBuckets(buckets);

        _bucketsByKey = new Dictionary<string, List<int>>(_buckets.Length, StringComparer.Ordinal);
        for (var i = 0; i < _buckets.Length; i++)
        {
            var key = CatalogTables.ReadKey(_keys, _buckets[i].KeyOffset);
            if (key.Length == 0) continue;
            if (!_bucketsByKey.TryGetValue(key, out var list))
                _bucketsByKey[key] = list = [];
            list.Add(i);
        }

        var bundles = ReadBundles(extra);
        Bundles = bundles;
        _byHash = bundles.ToDictionary(b => b.Hash, StringComparer.OrdinalIgnoreCase);
        _byCacheName = bundles
            .GroupBy(b => b.CacheName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string FilePath { get; }

    /// The catalog's own id, which names the build it came from.
    public string LocatorId { get; }

    public IReadOnlyList<CatalogBundle> Bundles { get; }

    /// Every key that reads as text: the addresses the game asks for, the asset paths behind them,
    /// and the labels. The rest are the hash codes a dependency list is filed under.
    public IEnumerable<string> Keys => _bucketsByKey.Keys;

    public CatalogBundle? ByHash(string hash)
        => _byHash.TryGetValue(hash, out var bundle) ? bundle : null;

    public CatalogBundle? ByCacheName(string cacheName)
        => _byCacheName.TryGetValue(cacheName, out var bundle) ? bundle : null;

    /// The bundles an address needs, itself included.
    ///
    /// A location names the key its dependencies are filed under rather than listing them, so this
    /// is two hops: the address's bucket gives its locations, and each location's dependency key
    /// gives a bucket whose locations are the bundles.
    public IReadOnlyList<CatalogBundle> BundlesFor(string key)
    {
        if (!_bucketsByKey.TryGetValue(key, out var buckets)) return [];

        var found = new List<CatalogBundle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
            foreach (var location in _buckets[bucket].Entries)
            {
                var entry = _entries[location];
                if (entry.DependencyKey < 0 || entry.DependencyKey >= _buckets.Length) continue;
                foreach (var dependency in _buckets[entry.DependencyKey].Entries)
                {
                    var id = _internalIds[_entries[dependency].InternalId];
                    if (!seen.Add(id)) continue;
                    if (BundleFor(id) is { } bundle) found.Add(bundle);
                }
            }
        return found;
    }

    /// Whether this catalog knows the key at all, which is how a session tells an installation that
    /// has moved to Addressables from one that has not.
    public bool Knows(string key) => _bucketsByKey.ContainsKey(key);

    private CatalogBundle? BundleFor(string internalId)
    {
        var name = internalId.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (!name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) return null;
        var hash = name[..^".bundle".Length];
        hash = hash[(hash.LastIndexOf('_') + 1)..];
        return ByHash(hash);
    }

    /// Each bundle's options, read through the locations that carry them. Only a bundle has an
    /// object in the extra-data blob; every other location points at nothing.
    private List<CatalogBundle> ReadBundles(byte[] extra)
    {
        var bundles = new List<CatalogBundle>();
        var seen = new HashSet<int>();
        foreach (var entry in _entries)
        {
            if (entry.DataIndex < 0 || !seen.Add(entry.DataIndex)) continue;
            if (CatalogTables.ReadJsonObject(extra, entry.DataIndex) is not { } json) continue;

            var options = JsonSerializer.Deserialize<RequestOptions>(json, OptionsJson);
            if (options?.Hash is not { Length: > 0 }) continue;

            var id = _internalIds[entry.InternalId];
            var name = id.Replace('\\', '/');
            bundles.Add(new CatalogBundle(
                FileName: name[(name.LastIndexOf('/') + 1)..],
                CacheName: options.BundleName ?? "",
                Hash: options.Hash,
                Crc: options.Crc,
                Size: options.BundleSize,
                IsRemote: id.StartsWith("http", StringComparison.OrdinalIgnoreCase),
                InternalId: id));
        }
        return bundles;
    }

    public static ContentCatalog Read(string catalogPath)
    {
        Raw raw;
        try
        {
            using var stream = File.OpenRead(catalogPath);
            raw = JsonSerializer.Deserialize<Raw>(stream, CatalogJson)
                ?? throw new InvalidDataException($"Could not parse '{catalogPath}'.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(
                $"The game's Addressables catalog at '{catalogPath}' is not readable: {e.Message}. "
                + "Verifying the game's files through its store puts it back.", e);
        }

        try
        {
            return new ContentCatalog(
                catalogPath, raw.LocatorId ?? "",
                Convert.FromBase64String(raw.KeyDataString ?? ""),
                Convert.FromBase64String(raw.BucketDataString ?? ""),
                Convert.FromBase64String(raw.EntryDataString ?? ""),
                Convert.FromBase64String(raw.ExtraDataString ?? ""),
                raw.InternalIds ?? []);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException(
                $"The game's Addressables catalog at '{catalogPath}' is not laid out as this reads "
                + $"it: {e.Message}.", e);
        }
    }

    /// Where the catalog sits in an installation, and the file whose contents name the build it is
    /// current for. The hash is an identifier rather than a hash of the catalog, so editing the
    /// catalog does not have to touch it.
    public const string FileName = "catalog.json";
    public const string HashFileName = "catalog.hash";

    public static string DirectoryIn(string dataDirectory)
        => Path.Combine(dataDirectory, "StreamingAssets", "aa");

    public static string PathIn(string dataDirectory)
        => Path.Combine(DirectoryIn(dataDirectory), FileName);

    /// Where the bundles the catalog calls local are kept.
    public static string LocalBundlesIn(string dataDirectory)
        => Path.Combine(DirectoryIn(dataDirectory), "StandaloneWindows64");

    private sealed record RequestOptions
    {
        [JsonPropertyName("m_Hash")] public string? Hash { get; init; }
        [JsonPropertyName("m_Crc")] public uint Crc { get; init; }
        [JsonPropertyName("m_BundleName")] public string? BundleName { get; init; }
        [JsonPropertyName("m_BundleSize")] public long BundleSize { get; init; }
    }

    private sealed record Raw
    {
        [JsonPropertyName("m_LocatorId")] public string? LocatorId { get; init; }
        [JsonPropertyName("m_InternalIds")] public string[]? InternalIds { get; init; }
        [JsonPropertyName("m_ProviderIds")] public string[]? ProviderIds { get; init; }
        [JsonPropertyName("m_KeyDataString")] public string? KeyDataString { get; init; }
        [JsonPropertyName("m_BucketDataString")] public string? BucketDataString { get; init; }
        [JsonPropertyName("m_EntryDataString")] public string? EntryDataString { get; init; }
        [JsonPropertyName("m_ExtraDataString")] public string? ExtraDataString { get; init; }
    }

    private static readonly JsonSerializerOptions CatalogJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions OptionsJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
