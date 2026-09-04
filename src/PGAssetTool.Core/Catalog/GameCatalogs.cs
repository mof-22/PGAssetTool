using AssetsTools.NET;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// The game's registries, each a MonoBehaviour in its own small bundle. Loading all of them takes
/// roughly a third of a second, which is why item lookups need no precomputed index.
public sealed class GameCatalogs
{
    public const string DefaultLanguage = "l_en-gb";

    private GameCatalogs(ItemCatalog items, AssetLookup lookup, Localization localization, SkinCatalog skins)
        => (Items, Lookup, Localization, Skins) = (items, lookup, localization, skins);

    public ItemCatalog Items { get; }
    public AssetLookup Lookup { get; }
    public Localization Localization { get; }
    public SkinCatalog Skins { get; }

    public static GameCatalogs Load(BundleSet bundles, string language = DefaultLanguage) => new(
        ItemCatalog.Load(bundles),
        AssetLookup.Load(bundles),
        Localization.Load(bundles, language),
        SkinCatalog.Load(bundles));
}

/// Maps a logical asset path such as "Weapons/Weapon25" to the bundle holding it. The game resolves
/// these strings at runtime, so following binary PPtr references alone leaves the graph disconnected.
public sealed class AssetLookup
{
    private readonly Dictionary<string, string> _bundleByPath;

    private AssetLookup(Dictionary<string, string> bundleByPath) => _bundleByPath = bundleByPath;

    public static AssetLookup Load(BundleSet bundles)
    {
        var entries = bundles.MonoBehaviour("assets_lookup_table", "*")["TableEntries"]["Array"];
        // Paths collide when compared case-insensitively; the first entry wins, as with localization.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Children)
            map.TryAdd(e["AssetPath"].AsString, e["BundleName"].AsString);
        return new AssetLookup(map);
    }

    /// Skin definitions store material paths relative to these roots rather than in full.
    public static readonly string[] SkinAssetRoots =
        ["WeaponSkinsV2/WeaponSkinAssets/", "WeaponSkinsV2/CustomModels/", "WeaponSkinsV2/"];

    public int Count => _bundleByPath.Count;
    public string? BundleFor(string assetPath) => _bundleByPath.GetValueOrDefault(assetPath);
    public IEnumerable<KeyValuePair<string, string>> Entries => _bundleByPath;

    /// Resolves a path that may be written relative to one of the given roots. Returns the full
    /// path that matched along with its bundle.
    public (string Path, string Bundle)? Resolve(string assetPath, params string[] roots)
    {
        if (_bundleByPath.TryGetValue(assetPath, out var direct)) return (assetPath, direct);
        foreach (var root in roots)
            if (_bundleByPath.TryGetValue(root + assetPath, out var prefixed))
                return (root + assetPath, prefixed);
        return null;
    }
}

/// Display strings for one language. Terms are duplicated in the source data; the first wins.
public sealed class Localization
{
    private readonly Dictionary<string, string> _terms;

    private Localization(string language, Dictionary<string, string> terms)
        => (Language, _terms) = (language, terms);

    public string Language { get; }

    public static Localization Load(BundleSet bundles, string language)
    {
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        var array = bundles.MonoBehaviour(language, "*")["mTerms"]["Array"];
        foreach (var e in array.Children)
            terms.TryAdd(e["term"].AsString, e["translation"].AsString);
        return new Localization(language, terms);
    }

    public string? Translate(string? term) => term is null ? null : _terms.GetValueOrDefault(term);
}
