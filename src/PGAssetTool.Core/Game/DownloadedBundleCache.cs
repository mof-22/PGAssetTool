namespace PGAssetTool.Core.Game;

/// The bundle cache the game fills at runtime, under the player's profile rather than the install.
///
/// Its ledger is Cache/Info/names, a list of "&lt;bundle&gt;/&lt;hash&gt;" lines. The game reads that to decide
/// what it already has: a bundle listed there is loaded from here, and one that is not comes from
/// the copy shipped under StreamingAssets. Deleting a listed bundle's file without removing its line
/// makes the game log "Failed to read data for the AssetBundle" and fall back to the shipped copy,
/// which is how the priority was pinned down.
///
/// This matters because editing the shipped copy of a bundle listed here changes nothing at all, and
/// says nothing about it.
public sealed class DownloadedBundleCache
{
    private readonly Dictionary<string, string> _claimed;

    private DownloadedBundleCache(string root, Dictionary<string, string> claimed)
    {
        Root = root;
        _claimed = claimed;
    }

    /// The Cache directory holding both bundles/ and Info/names.
    public string Root { get; }

    public string BundlesDirectory => Path.Combine(Root, "bundles");
    public string NamesPath => Path.Combine(Root, "Info", "names");

    /// Bundle name to the hash the ledger claims, whether or not the file is actually there.
    public IReadOnlyDictionary<string, string> Claimed => _claimed;

    /// Unity builds its persistent data path from the company and product names, and app.info holds
    /// exactly those two lines.
    public static DownloadedBundleCache? Find(string dataDirectory)
    {
        var appInfo = Path.Combine(dataDirectory, "app.info");
        if (!File.Exists(appInfo)) return null;

        var lines = File.ReadAllLines(appInfo);
        if (lines.Length < 2) return null;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", lines[0].Trim(), lines[1].Trim(), "Cache");
        return Directory.Exists(root) ? Open(root) : null;
    }

    public static DownloadedBundleCache Open(string root)
    {
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = Path.Combine(root, "Info", "names");
        if (File.Exists(names))
        {
            foreach (var line in File.ReadAllLines(names))
            {
                var slash = line.LastIndexOf('/');
                if (slash <= 0 || slash == line.Length - 1) continue;
                claimed[line[..slash].Trim()] = line[(slash + 1)..].Trim();
            }
        }
        return new DownloadedBundleCache(root, claimed);
    }

    public string PathOf(string bundle, string hash)
        => Path.Combine(BundlesDirectory, bundle, hash, bundle);

    /// The path the game would load from here, or null when this cache does not claim the bundle.
    public string? ClaimedPath(string bundle)
        => _claimed.TryGetValue(bundle, out var hash) ? PathOf(bundle, hash) : null;

    /// Bundles the ledger claims but whose file is gone. The game logs an error for each of these
    /// before falling back, so they are worth surfacing.
    public IEnumerable<string> ClaimedButMissing()
        => _claimed.Where(kv => !File.Exists(PathOf(kv.Key, kv.Value))).Select(kv => kv.Key);
}
