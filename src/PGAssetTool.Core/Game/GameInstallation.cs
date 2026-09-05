using System.Text.Json;

namespace PGAssetTool.Core.Game;

public sealed record BundleEntry(string Name, string Hash)
{
    /// Bundles live at &lt;bundles&gt;/&lt;name&gt;/&lt;hash&gt;/&lt;name&gt; (Unity's download cache layout).
    public string PathUnder(string bundlesDirectory) => Path.Combine(bundlesDirectory, Name, Hash, Name);
}

/// A game installation on disk. Accepts any directory with the expected layout, so a copy of the
/// game data can be used instead of the live install.
public sealed class GameInstallation
{
    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string BundlesDirectory { get; }

    private GameInstallation(string root, string data, string bundles)
        => (RootDirectory, DataDirectory, BundlesDirectory) = (root, data, bundles);

    public static GameInstallation Open(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var data = Directory.EnumerateDirectories(root, "*_Data").FirstOrDefault()
            ?? throw new DirectoryNotFoundException($"No '*_Data' directory under '{root}'.");
        var bundles = Path.Combine(data, "StreamingAssets", "Cache", "bundles");
        if (!Directory.Exists(bundles))
            throw new DirectoryNotFoundException($"Bundle cache not found at '{bundles}'.");
        return new GameInstallation(root, data, bundles);
    }

    public static GameInstallation OpenDetected()
        => Open(SteamLocator.FindGameDirectory()
            ?? throw new DirectoryNotFoundException(
                "Could not locate the game through Steam. Pass the install directory explicitly."));

    public string ManifestPath => Path.Combine(BundlesDirectory, "embedded_asset_bundles.json");

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
