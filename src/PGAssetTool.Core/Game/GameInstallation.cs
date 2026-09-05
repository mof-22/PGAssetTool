using System.Text.Json;

namespace PGAssetTool.Core.Game;

public sealed record BundleEntry(string Name, string Hash);

/// A game installation on disk. Accepts any directory with the expected layout, so a copy of the
/// game data can be used instead of the live install.
public sealed class GameInstallation
{
    public string RootDirectory { get; }
    public string DataDirectory { get; }

    /// Bundles live in two places. The game ships a full set under StreamingAssets and downloads
    /// updates into the player's profile at runtime; where a bundle exists in both, the downloaded
    /// copy is the one loaded. Editing only the shipped copy changes nothing, silently.
    public IReadOnlyList<BundleCache> Caches { get; }

    private GameInstallation(string root, string data, IReadOnlyList<BundleCache> caches)
        => (RootDirectory, DataDirectory, Caches) = (root, data, caches);

    public BundleCache ShippedCache => Caches.First(c => c.Kind == CacheKind.Shipped);
    public BundleCache? DownloadedCache => Caches.FirstOrDefault(c => c.Kind == CacheKind.Downloaded);

    /// Highest priority first, so the first hit is what the game would load.
    public IEnumerable<BundleCache> InLoadOrder =>
        Caches.OrderBy(c => c.Kind == CacheKind.Downloaded ? 0 : 1);

    public static GameInstallation Open(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var data = Directory.EnumerateDirectories(root, "*_Data").FirstOrDefault()
            ?? throw new DirectoryNotFoundException($"No '*_Data' directory under '{root}'.");

        var shipped = Path.Combine(data, "StreamingAssets", "Cache", "bundles");
        if (!Directory.Exists(shipped))
            throw new DirectoryNotFoundException($"Bundle cache not found at '{shipped}'.");

        var caches = new List<BundleCache> { new(CacheKind.Shipped, shipped) };
        if (FindDownloadedCache(data) is { } downloaded)
            caches.Add(new BundleCache(CacheKind.Downloaded, downloaded));

        return new GameInstallation(root, data, caches);
    }

    public static GameInstallation OpenDetected()
        => Open(SteamLocator.FindGameDirectory()
            ?? throw new DirectoryNotFoundException(
                "Could not locate the game through Steam. Pass the install directory explicitly."));

    /// Unity puts persistent data under LocalLow/&lt;company&gt;/&lt;product&gt;, and app.info holds exactly
    /// those two names.
    private static string? FindDownloadedCache(string dataDirectory)
    {
        var appInfo = Path.Combine(dataDirectory, "app.info");
        if (!File.Exists(appInfo)) return null;

        var lines = File.ReadAllLines(appInfo);
        if (lines.Length < 2) return null;

        var localLow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", lines[0].Trim(), lines[1].Trim());
        var bundles = Path.Combine(localLow, "Cache", "bundles");
        return Directory.Exists(bundles) ? bundles : null;
    }

    public string ManifestPath => Path.Combine(ShippedCache.Directory, "embedded_asset_bundles.json");

    /// The name→hash manifest the game ships. Its contents change whenever any bundle is updated,
    /// which is what drives incremental re-indexing.
    public IReadOnlyList<BundleEntry> ReadManifest()
    {
        using var stream = File.OpenRead(ManifestPath);
        return JsonSerializer.Deserialize<List<BundleEntry>>(stream, ManifestJsonOptions)
            ?? throw new InvalidDataException($"Could not parse '{ManifestPath}'.");
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// Every copy of a bundle that exists, in the order the game would prefer them.
    public IEnumerable<(BundleCache Cache, string Path)> LocateAll(string bundle, string hash)
    {
        foreach (var cache in InLoadOrder)
            if (cache.Has(bundle, hash))
                yield return (cache, cache.PathOf(bundle, hash));
    }

    public (BundleCache Cache, string Path)? Locate(string bundle, string hash)
    {
        foreach (var found in LocateAll(bundle, hash)) return found;
        return null;
    }

    /// Serialized files that sit outside the bundle caches (resources.assets, sharedassets*, levels).
    /// Weapon icons are split across these and the bundles, so both are indexed.
    public IEnumerable<string> EnumerateSerializedFiles()
        => Directory.EnumerateFiles(DataDirectory)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return n.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase)
                    || (n.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                        && n.Length > 5 && char.IsAsciiDigit(n[5]));
            })
            .Order(StringComparer.OrdinalIgnoreCase);

    public string GlobalGameManagersPath => Path.Combine(DataDirectory, "globalgamemanagers");
}
