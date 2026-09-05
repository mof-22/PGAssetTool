namespace PGAssetTool.Core.Game;

public enum CacheKind
{
    /// Ships with the game under StreamingAssets.
    Shipped,

    /// Downloaded at runtime into the player's profile. Wins over the shipped copy.
    Downloaded,
}

/// One directory of bundles, laid out as &lt;name&gt;/&lt;hash&gt;/&lt;name&gt;.
public sealed record BundleCache(CacheKind Kind, string Directory)
{
    public string PathOf(string bundle, string hash)
        => Path.Combine(Directory, bundle, hash, bundle);

    public bool Has(string bundle, string hash) => File.Exists(PathOf(bundle, hash));

    /// The bundle versions this cache holds, whatever the manifest says the current one is.
    public IEnumerable<(string Bundle, string Hash)> Enumerate()
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        foreach (var bundleDirectory in System.IO.Directory.EnumerateDirectories(Directory))
        {
            var bundle = Path.GetFileName(bundleDirectory);
            foreach (var hashDirectory in System.IO.Directory.EnumerateDirectories(bundleDirectory))
                if (File.Exists(Path.Combine(hashDirectory, bundle)))
                    yield return (bundle, Path.GetFileName(hashDirectory));
        }
    }
}
