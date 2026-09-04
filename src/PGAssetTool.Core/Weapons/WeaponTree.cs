using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Weapons;

public sealed record WeaponSkinView(SkinRecord Record, string? DisplayName);

public sealed record WeaponTree(
    WeaponRecord Record,
    string DisplayName,
    string? PrefabBundle,
    IReadOnlyList<AssetNode> PrefabAssets,
    IReadOnlyList<WeaponSkinView> Skins,
    IReadOnlyList<string> UnresolvedReasons);

/// Assembles everything belonging to one weapon. Resolution happens on demand: the catalogs plus
/// the single bundle holding the prefab are enough, so nothing is precomputed.
public sealed class WeaponResolver(BundleSet bundles, GameCatalogs catalogs)
{
    public WeaponTree Resolve(WeaponRecord record)
    {
        var unresolved = new List<string>();

        var displayName = catalogs.Localization.Translate(record.LocalizationKey);
        if (displayName is null)
            unresolved.Add($"no translation for '{record.LocalizationKey}' in {catalogs.Localization.Language}");

        var prefabPath = "Weapons/" + record.PrefabName;
        var prefabBundle = catalogs.Lookup.BundleFor(prefabPath);
        if (prefabBundle is null) unresolved.Add($"'{prefabPath}' is not in the asset lookup table");

        var assets = new List<AssetNode>();
        if (prefabBundle is not null)
        {
            var file = bundles.Open(prefabBundle);
            var root = ReferenceWalker.FindByName(
                bundles.Context, file, AssetClassID.GameObject, record.PrefabName);
            if (root is null)
                unresolved.Add($"'{record.PrefabName}' was not found inside bundle '{prefabBundle}'");
            else
                assets = ReferenceWalker.Closure(bundles.Context, file, root.PathId);
        }

        var skins = catalogs.Skins.ForWeapon(record.Index)
            .Select(s => new WeaponSkinView(s, catalogs.Localization.Translate(s.LocalizationKey)))
            .ToList();

        return new WeaponTree(record, displayName ?? record.Slug, prefabBundle, assets, skins, unresolved);
    }
}
