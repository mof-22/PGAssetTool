namespace PGAssetTool.Core.Addressables;

/// The cache Addressables fills as the player plays, under their profile rather than the install.
///
/// A bundle is kept at &lt;root&gt;/&lt;cache name&gt;/&lt;hash&gt;/__data, and the `__info` file beside
/// it holds no name — so nothing here can be listed into something readable without the catalog.
/// What is cached is whatever the player has reached: the game fetches a weapon or a map when it is
/// picked, and offers no way to fetch the rest.
public sealed class AddressablesCache
{
    public const string DirectoryName = "AddressablesCache";
    public const string DataFileName = "__data";

    private AddressablesCache(string root) => Root = root;

    public string Root { get; }

    /// Unity builds the profile path from the company and product names, and app.info holds exactly
    /// those two lines. The same two lines the old bundle cache is found by.
    public static AddressablesCache? Find(string dataDirectory)
    {
        var appInfo = Path.Combine(dataDirectory, "app.info");
        if (!File.Exists(appInfo)) return null;

        var lines = File.ReadAllLines(appInfo);
        if (lines.Length < 2) return null;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", lines[0].Trim(), lines[1].Trim(), DirectoryName);
        return Directory.Exists(root) ? new AddressablesCache(root) : null;
    }

    /// Opens a specific directory instead of the one under the player's profile, for tests and for
    /// pointing at a copy of it.
    public static AddressablesCache Open(string root) => new(root);

    public string PathOf(CatalogBundle bundle) => bundle.PathUnder(Root);

    public bool Holds(CatalogBundle bundle) => File.Exists(PathOf(bundle));

    /// How many bundles are in the cache, counted as the directories that hold one. A directory
    /// with no `__data` is a download that has not finished.
    public int Count()
        => Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root)
                .Count(d => Directory.EnumerateDirectories(d)
                    .Any(v => File.Exists(Path.Combine(v, DataFileName))))
            : 0;
}

/// Where the game would load a catalog bundle from, and whether it is there to load.
public sealed record ResolvedCatalogBundle(CatalogBundle Bundle, string Path, bool IsCached);

/// Resolving a catalog bundle against an installation: the cache for what the game downloads, the
/// install for what it ships.
///
/// Kept apart from `GameInstallation.Resolve`, which answers for the old bundle path. The two
/// layouts share nothing — not the directories, not the hashes, not the manifest — and a 26.15
/// installation has both at once, 90 bundles on the old path and 17,988 on this one.
public sealed class AddressableBundles
{
    private readonly string _localDirectory;

    public AddressableBundles(ContentCatalog catalog, string dataDirectory, AddressablesCache? cache)
    {
        Catalog = catalog;
        Cache = cache;
        _localDirectory = ContentCatalog.LocalBundlesIn(dataDirectory);
    }

    public ContentCatalog Catalog { get; }

    /// Present once the game has downloaded anything, which on a fresh install is at once.
    public AddressablesCache? Cache { get; }

    public static AddressableBundles For(string dataDirectory)
        => new(ContentCatalog.Read(ContentCatalog.PathIn(dataDirectory)),
            dataDirectory, AddressablesCache.Find(dataDirectory));

    /// Where this bundle is on disk, or null for one the game has not fetched.
    ///
    /// A bundle the catalog calls remote is only ever in the cache; one it calls local is only ever
    /// in the install. There is no falling back from one to the other: the catalog says which it is
    /// and the game does not look anywhere else.
    public ResolvedCatalogBundle? Resolve(CatalogBundle bundle)
    {
        if (bundle.IsRemote)
            return Cache?.PathOf(bundle) is { } cached && File.Exists(cached)
                ? new ResolvedCatalogBundle(bundle, cached, IsCached: true)
                : null;

        var shipped = bundle.PathIn(_localDirectory);
        return File.Exists(shipped)
            ? new ResolvedCatalogBundle(bundle, shipped, IsCached: false)
            : null;
    }

    /// The bundles an address needs that are on disk, and the names of those that are not. An item
    /// whose bundles are all missing is one the player has never reached.
    public (IReadOnlyList<ResolvedCatalogBundle> Found, IReadOnlyList<string> Missing) ResolveFor(string key)
    {
        var found = new List<ResolvedCatalogBundle>();
        var missing = new List<string>();
        foreach (var bundle in Catalog.BundlesFor(key))
        {
            if (Resolve(bundle) is { } resolved) found.Add(resolved);
            else missing.Add(bundle.FileName);
        }
        return (found, missing);
    }
}
