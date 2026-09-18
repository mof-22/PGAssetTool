using System.Text.Json;

namespace PGAssetTool.Core.Game;

public sealed record BundleEntry(string Name, string Hash)
{
    /// Bundles live at &lt;bundles&gt;/&lt;name&gt;/&lt;hash&gt;/&lt;name&gt; (Unity's download cache layout).
    public string PathUnder(string bundlesDirectory) => Path.Combine(bundlesDirectory, Name, Hash, Name);
}

/// A game installation on disk. Accepts any directory with the expected layout, so a copy of the
/// game data can be used instead of the live install.
public enum CacheKind
{
    /// Shipped with the game under StreamingAssets.
    Shipped,

    /// Downloaded into the player's profile at runtime. Takes precedence.
    Downloaded,
}

public sealed record ResolvedBundle(CacheKind Cache, string Path);

public sealed class GameInstallation
{
    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string BundlesDirectory { get; }

    /// Present once the game has downloaded anything. Its ledger decides which copy of a bundle is
    /// live, so resolution has to go through it rather than assuming the shipped copy.
    public DownloadedBundleCache? Downloaded { get; }

    private GameInstallation(string root, string data, string bundles, DownloadedBundleCache? downloaded)
        => (RootDirectory, DataDirectory, BundlesDirectory, Downloaded) = (root, data, bundles, downloaded);

    public static GameInstallation Open(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var data = Directory.EnumerateDirectories(root, "*_Data").FirstOrDefault()
            ?? throw new DirectoryNotFoundException($"No '*_Data' directory under '{root}'.");
        var bundles = Path.Combine(data, "StreamingAssets", "Cache", "bundles");
        if (!Directory.Exists(bundles))
            throw new DirectoryNotFoundException($"Bundle cache not found at '{bundles}'.");
        return new GameInstallation(root, data, bundles, DownloadedBundleCache.Find(data));
    }

    /// Opens with a specific downloaded cache instead of the one under the player's profile, for
    /// tests and for pointing at a copy of it.
    public static GameInstallation OpenWith(string rootDirectory, DownloadedBundleCache? downloaded)
    {
        var opened = Open(rootDirectory);
        return new GameInstallation(
            opened.RootDirectory, opened.DataDirectory, opened.BundlesDirectory, downloaded);
    }

    /// Where the game would load this bundle from. The downloaded cache wins when its ledger claims
    /// the bundle and the file is really there; a claim with no file makes the game log an error and
    /// fall back, so that case resolves to the shipped copy too.
    public ResolvedBundle? Resolve(string bundle, string hash)
    {
        if (Downloaded?.ClaimedPath(bundle) is { } claimed && File.Exists(claimed))
            return new ResolvedBundle(CacheKind.Downloaded, claimed);

        var shipped = Path.Combine(BundlesDirectory, bundle, hash, bundle);
        return File.Exists(shipped) ? new ResolvedBundle(CacheKind.Shipped, shipped) : null;
    }

    public static GameInstallation OpenDetected()
        => Open(SteamLocator.FindGameDirectory()
            ?? throw new DirectoryNotFoundException(
                "Could not locate the game through Steam. Pass the install directory explicitly."));

    public const string ManifestFileName = "embedded_asset_bundles.json";

    public string ManifestPath => Path.Combine(BundlesDirectory, ManifestFileName);

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

    /// Serialized files that sit outside the bundle cache (resources.assets, sharedassets*, levels).
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
