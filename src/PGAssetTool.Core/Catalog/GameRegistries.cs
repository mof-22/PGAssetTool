using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Addressables;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// One of the game's registries: a ScriptableObject it keeps a table of items in.
///
/// The two layouts name the same object differently — a bundle in the manifest and the behaviour's
/// own name on the old path, an addressable key on the new one — so a registry carries both names
/// and the reader picks by which layout it was given.
public sealed record Registry(string Bundle, string Behaviour, string Address);

/// Where the game keeps each of them.
///
/// The old path's copies are **still readable and no longer current**: the bundles are there, the
/// behaviours parse, and they describe the game as it was before 26.15. Measured against the
/// author's installation: 1,523 weapons against the catalog's 1,530, 5,346 item records against
/// 5,374, 247 weapons with skins against 248. That is why the newest weapons were missing from the
/// tool while the game listed them.
public static class Registries
{
    public static readonly Registry Items =
        new("it_d", "ItemsDataStorage", "CommonData/ItemsDataStorage");

    public static readonly Registry WeaponSkins =
        new("ws_d", "WeaponSkinsDataStorage", "WeaponSkins/WeaponSkinsDataStorage");

    public static readonly Registry Wear =
        new("generated_data", "Wear_GENERATED", "ScriptableObjects/Wear_GENERATED");

    public static readonly Registry Pets =
        new("generated_data", "Pets_GENERATED", "ScriptableObjects/Pets_GENERATED");

    public static readonly Registry Royale =
        new("generated_data", "Royale_GENERATED", "ScriptableObjects/Royale_GENERATED");

    public static readonly Registry WeaponSkinIndices = new(
        "generated_data", "WeaponSkinItemTypeIndices_GENERATED",
        "ScriptableObjects/WeaponSkinItemTypeIndices_GENERATED");

    /// The term tables, by the bundle the old path keeps each language in.
    ///
    /// The game did not change localization component when it moved: the same table of terms is in
    /// an addressable prefab now, `Localization/Language_&lt;Name&gt;`, one per language and the same
    /// eleven. What is left on the old path is **not merely short** — the keys are `Key_&lt;n&gt;`
    /// and the stale table has different text under the numbers the newest items use, so it answers
    /// a weapon's name with another item's. `final_offer` read as "SPECIAL" that way.
    ///
    /// Keyed by the old bundle name because that is what a saved setting holds and what the picker
    /// offers; there is no reason to make anyone choose their language again.
    public static readonly IReadOnlyDictionary<string, string> Languages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["l_de"] = "Localization/Language_German",
            ["l_en-gb"] = "Localization/Language_English",
            ["l_es"] = "Localization/Language_Spanish",
            ["l_fr"] = "Localization/Language_French",
            ["l_ja"] = "Localization/Language_Japanese",
            ["l_ko"] = "Localization/Language_Korean",
            ["l_pt-br"] = "Localization/Language_Portuguese (brazil)",
            ["l_ru-mo"] = "Localization/Language_Russian",
            ["l_tr"] = "Localization/Language_Turkish",
            ["l_zh"] = "Localization/Language_Chinese (chinese)",
            ["l_zh-cht"] = "Localization/Language_ChineseTraditional",
        };
}

/// Reads the registries out of whichever layout this installation keeps them in.
///
/// Given a content reader, the catalog is asked first and the old bundles stand in for anything it
/// has not got; given none, the old bundles are all there is. The registries live in **shipped**
/// bundles on the new path — `coredata` holds four of them — so nothing here waits on a download.
public sealed class RegistryReader
{
    private readonly BundleSet _bundles;
    private readonly AddressableSet? _content;

    public RegistryReader(BundleSet bundles, AddressableSet? content = null)
        => (_bundles, _content) = (bundles, content);

    public AssetTypeValueField Read(Registry which)
        => TryRead(which)
           ?? throw new KeyNotFoundException(
               $"The game's '{which.Behaviour}' registry was not found, in the catalog as "
               + $"'{which.Address}' or in bundle '{which.Bundle}'.");

    public AssetTypeValueField? TryRead(Registry which) => FromCatalog(which) ?? FromBundle(which);

    private AssetTypeValueField? FromCatalog(Registry which)
    {
        if (_content is not { } content) return null;

        try
        {
            // An address can cover a whole group, so the location is picked by the asset it names
            // rather than taken as the only one.
            var location = content.Catalog.LocationsFor(which.Address).FirstOrDefault(
                l => l.InternalId.EndsWith($"/{which.Behaviour}.asset", StringComparison.OrdinalIgnoreCase));
            if (location is null) return null;

            foreach (var bundle in location.Bundles)
            {
                if (!content.Holds(bundle)) continue;
                if (content.Locate(bundle, location.InternalId) is not { } node) continue;

                var file = content.Open(bundle);
                if (file.file.GetAssetInfo(node.PathId) is not { } info) continue;
                if (content.Context.Deserialize(file, info) is { } field) return field;
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException)
        {
            // An unreadable catalog copy is not a reason to fail: the old one is tried below, and
            // what it says is stale rather than nothing.
        }

        return null;
    }

    private AssetTypeValueField? FromBundle(Registry which)
    {
        try { return _bundles.TryMonoBehaviour(which.Bundle, which.Behaviour); }
        catch (Exception e) when (e is IOException or KeyNotFoundException or NotSupportedException)
        {
            return null;
        }
    }

    /// The term table for a language, out of the catalog's copy.
    ///
    /// A language is a prefab rather than a behaviour, and the terms are on a component of it — so
    /// unlike the other registries this looks inside the bundle rather than at the object the
    /// address names. There are four assets in one of these.
    public AssetTypeValueField? TryReadLanguage(string language)
    {
        if (_content is not { } content) return null;
        if (!Registries.Languages.TryGetValue(language, out var address)) return null;

        try
        {
            foreach (var location in content.Catalog.LocationsFor(address))
                foreach (var bundle in location.Bundles)
                {
                    if (!content.Holds(bundle)) continue;
                    if (TermsIn(content, bundle) is { } terms) return terms;
                }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException)
        {
        }

        return null;
    }

    private static AssetTypeValueField? TermsIn(AddressableSet content, CatalogBundle bundle)
    {
        var file = content.Open(bundle);
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetClassID.MonoBehaviour) continue;
            var field = content.Context.Deserialize(file, info);
            if (field is not null && !field["mTerms"]["Array"].IsDummy) return field;
        }
        return null;
    }

    /// Whether this installation's registries are being read from the catalog, which is what tells
    /// a 26.15 game from one before it.
    public bool FromTheCatalog => _content is not null && FromCatalog(Registries.Items) is not null;

    /// The same question for the term tables, which anything keeping a table of names between runs
    /// has to record: the old bundles keep their hashes whatever the catalog holds, so nothing else
    /// about a kept table says which copy it was built from.
    public bool ReadsLanguagesFromTheCatalog
        => _readsLanguages ??= Registries.Languages.Keys.Any(l => TryReadLanguage(l) is not null);

    private bool? _readsLanguages;
}
