using System.Text.RegularExpressions;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// The game's registries, each a MonoBehaviour in its own small bundle. Loading all of them takes
/// roughly a third of a second, which is why item lookups need no precomputed index.
public sealed class GameCatalogs
{
    public const string DefaultLanguage = "l_en-gb";

    private GameCatalogs(
        ItemCatalog items, AssetLookup lookup, Localization localization, SkinCatalog skins,
        GearCatalog gear)
        => (Items, Lookup, Localization, Skins, Gear) = (items, lookup, localization, skins, gear);

    /// Every weapon name in every language, for searching. Filled in after construction because it
    /// needs the item catalog that is being built alongside it.
    public WeaponNames? Names { get; private set; }

    /// Where these were read from, so reading them again in another language goes the same way.
    public RegistryReader? Registries { get; private init; }

    public ItemCatalog Items { get; }
    public AssetLookup Lookup { get; }
    public Localization Localization { get; }
    public SkinCatalog Skins { get; }

    /// Everything the game sells that is not a weapon, by kind.
    public GearCatalog Gear { get; }

    /// The items of one kind, whichever kind that is. Weapons come from their own registry and
    /// everything else from the generated ones, and nothing outside here needs to know that.
    public IReadOnlyList<WeaponRecord> Of(ItemKind kind)
        => kind == ItemKinds.Weapon ? Items.Weapons.ToList() : Gear.Of(kind);

    /// The kinds with anything in them, so a picker offers nothing that leads nowhere.
    public IReadOnlyList<ItemKind> Kinds
        => [.. ItemKinds.All.Where(k => k == ItemKinds.Weapon || Gear.Count(k) > 0)];

    /// Whatever the query names, of whatever kind.
    ///
    /// Weapons first and by their own rules — a bare number is an in-game number and nothing else —
    /// because that is what somebody typing at this tool almost always means. Everything else is
    /// found by the id the game knows it as, which is what the lists show and what a pack records.
    public WeaponRecord? Find(string query)
    {
        if (Items.Find(query) is { } weapon) return weapon;

        foreach (var kind in Kinds.Where(k => k != ItemKinds.Weapon))
            if (Gear.Of(kind).FirstOrDefault(
                    r => string.Equals(r.Slug, query, StringComparison.OrdinalIgnoreCase)) is { } found)
                return found;

        return null;
    }

    /// The game's own translation tables, as bundle name and the language in its own script.
    ///
    /// Read from the bundle list rather than hardcoded, so a language added by an update appears
    /// without a change here; one whose name is not known shows its bundle name instead of being
    /// hidden.
    public static IReadOnlyList<(string Bundle, string Name)> Languages(BundleSet bundles)
        => bundles.BundleNames
            .Where(b => b.StartsWith("l_", StringComparison.Ordinal))
            .OrderBy(b => b, StringComparer.Ordinal)
            .Select(b => (b, NativeNames.GetValueOrDefault(b, b)))
            .ToList();

    /// How each language calls itself, for a picker. Missing entries fall back to the bundle name.
    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["l_de"] = "Deutsch",
        ["l_en-gb"] = "English",
        ["l_es"] = "Español",
        ["l_fr"] = "Français",
        ["l_ja"] = "日本語",
        ["l_ko"] = "한국어",
        ["l_pt-br"] = "Português (Brasil)",
        ["l_ru-mo"] = "Русский",
        ["l_tr"] = "Türkçe",
        ["l_zh"] = "简体中文",
        ["l_zh-cht"] = "繁體中文",
    };

    /// <param name="remember">
    /// A directory the name tables may be kept in between runs — a tool's own data folder. Reading
    /// all eleven of the game's is 355ms of the 930 a window waits before it has a list, and they
    /// change only when the game does. Null reads them every time. See WeaponNames.
    /// </param>
    /// <param name="lookup">
    /// Where to ask which bundle holds a logical path. Left out, the registry the old route keeps
    /// that in; the Addressables route hands in one built from the content catalog instead, since a
    /// bundle name from the old registry means nothing to its reader. Everything else here — the
    /// items, the names, the skins, the gear — is read the same way on both, out of the registries
    /// the game still ships in its own small bundles.
    /// </param>
    /// <param name="registries">
    /// Where to read the game's item tables from. Left out, the bundles the old path keeps them in
    /// — which on a 26.15 installation are **still readable and out of date**: they describe the
    /// game before the move, seven weapons short. See `RegistryReader`.
    /// </param>
    public static GameCatalogs Load(
        BundleSet bundles, string language = DefaultLanguage, string? remember = null,
        AssetLookup? lookup = null, RegistryReader? registries = null)
    {
        registries ??= new RegistryReader(bundles);
        var catalogs = new GameCatalogs(
            ItemCatalog.Load(registries),
            lookup ?? AssetLookup.Load(bundles),
            Localization.Load(bundles, language, registries),
            SkinCatalog.Load(bundles, registries),
            GearCatalog.Load(registries))
        {
            Registries = registries,
        };

        catalogs.Names = WeaponNames.Load(bundles, catalogs.Items, remember, registries);
        return catalogs;
    }

    /// The same registries read again in another language.
    ///
    /// Only the translation table depends on the language; the items, the lookup table, the skins
    /// and the search index do not. Rebuilding all of them to change which name is displayed costs
    /// the better part of a second for no reason.
    public GameCatalogs WithLanguage(BundleSet bundles, string language)
        => new(Items, Lookup, Localization.Load(bundles, language, Registries), Skins, Gear)
        {
            Names = Names,
            Registries = Registries,
        };
}

/// Maps a logical asset path such as "Weapons/Weapon25" to the bundle holding it. The game resolves
/// these strings at runtime, so following binary PPtr references alone leaves the graph disconnected.
/// Both of the game's layouts answer this question and neither one's answer means anything to the
/// other reader, so a lookup is built with the paths it knows and a way of asking where one of them
/// is. `Load` reads the registry the old route keeps it in; the Addressables route builds one from
/// the catalog, where the same logical paths are the keys the game asks for.
public sealed partial class AssetLookup
{
    /// Every path in the order it was given, and the spelling each one is stored under.
    ///
    /// Two maps rather than one because the two halves of this disagree about case. A registry says
    /// `BerserkBoots_Up1` where the content catalog has `BerserkBoots_up1`, and the icon names do
    /// not match the slugs either — so a path has to be findable however it is spelt. But what is
    /// asked of the route has to be the spelling the route itself holds, since the catalog's keys
    /// are case-sensitive: asked for the caller's capitalisation it answers nothing, which is what
    /// left fourteen pairs of boots unresolved.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _spelling = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _bundleFor;
    private readonly Dictionary<int, List<string>> _pathsByWeapon;
    private readonly Dictionary<string, string?> _answered = new(StringComparer.Ordinal);

    /// <param name="paths">
    /// Every logical path this lookup knows, in the order they are to be offered. Paths collide when
    /// compared case-insensitively and the first spelling wins, as with localization.
    /// </param>
    /// <param name="bundleFor">
    /// Where one of those paths is, in whatever a bundle is called on this route, or null for a path
    /// nothing holds. Asked at most once per path.
    /// </param>
    public AssetLookup(IEnumerable<string> paths, Func<string, string?> bundleFor)
    {
        _bundleFor = bundleFor;
        _pathsByWeapon = [];

        foreach (var path in paths)
        {
            if (!_spelling.TryAdd(path, path)) continue;
            _order.Add(path);

            foreach (Match match in WeaponToken.Matches(path))
            {
                if (!int.TryParse(match.Groups[1].ValueSpan, out var number)) continue;
                if (!_pathsByWeapon.TryGetValue(number, out var list)) _pathsByWeapon[number] = list = [];
                if (!list.Contains(path)) list.Add(path);
            }
        }
    }

    /// A weapon's assets are spread over a dozen namespaces, tied together only by the number in
    /// their name. Extracting that token rather than enumerating known namespaces means namespaces
    /// added by a future update are picked up without a code change. Rays use both spellings.
    [GeneratedRegex(@"\b(?:Weapon|Ray)(\d+)")]
    private static partial Regex WeaponToken { get; }

    /// The registry the old route keeps this in, which is a MonoBehaviour in a bundle of its own.
    public static AssetLookup Load(BundleSet bundles)
    {
        var entries = bundles.MonoBehaviour("assets_lookup_table", "*")["TableEntries"]["Array"];
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Children)
            map.TryAdd(e["AssetPath"].AsString, e["BundleName"].AsString);

        return new AssetLookup(map.Keys, path => map.GetValueOrDefault(path));
    }

    /// Every asset path whose name carries this prefab number, in any namespace.
    public IReadOnlyList<string> PathsForWeapon(int prefabNumber)
        => _pathsByWeapon.GetValueOrDefault(prefabNumber) ?? (IReadOnlyList<string>)[];

    /// Skin definitions store material paths relative to these roots rather than in full.
    public static readonly string[] SkinAssetRoots =
        ["WeaponSkinsV2/WeaponSkinAssets/", "WeaponSkinsV2/CustomModels/", "WeaponSkinsV2/"];

    public int Count => _order.Count;

    /// Every path this lookup knows, in the order they were given.
    public IReadOnlyList<string> Paths => _order;

    public bool Knows(string assetPath) => _spelling.ContainsKey(assetPath);

    public string? BundleFor(string assetPath)
    {
        if (!_spelling.TryGetValue(assetPath, out var exact)) return null;
        if (_answered.TryGetValue(exact, out var already)) return already;
        return _answered[exact] = _bundleFor(exact);
    }

    /// Resolves a path that may be written relative to one of the given roots. Returns the full
    /// path that matched along with its bundle.
    public (string Path, string Bundle)? Resolve(string assetPath, params string[] roots)
    {
        if (BundleFor(assetPath) is { } direct) return (assetPath, direct);
        foreach (var root in roots)
            if (BundleFor(root + assetPath) is { } prefixed)
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

    public static Localization FromTerms(string language, IEnumerable<KeyValuePair<string, string>> terms)
        => new(language, terms.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal));

    /// <param name="registries">
    /// Where to read the table from. Given, the catalog's copy is used and the bundle on the old
    /// path stands in only where the catalog has none — and on a 26.15 game the old copy is stale
    /// in a way that shows as wrong names rather than missing ones. See `Registries.Languages`.
    /// </param>
    public static Localization Load(
        BundleSet bundles, string language, RegistryReader? registries = null)
    {
        var table = registries?.TryReadLanguage(language) ?? bundles.MonoBehaviour(language, "*");

        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in table["mTerms"]["Array"].Children)
            terms.TryAdd(e["term"].AsString, e["translation"].AsString);
        return new Localization(language, terms);
    }

    public string? Translate(string? term) => term is null ? null : _terms.GetValueOrDefault(term);
}
