using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Weapons;

public sealed record WeaponSkinView(SkinRecord Record, string? DisplayName);

/// An asset tied to the weapon by the number in its name rather than by a binary reference.
public sealed record RelatedAsset(string Namespace, string Path, string? Bundle);

public sealed record WeaponTree(
    WeaponRecord Record,
    string DisplayName,
    string? PrefabBundle,
    IReadOnlyList<AssetNode> PrefabAssets,
    IReadOnlyList<WeaponSkinView> Skins,
    IReadOnlyList<RelatedAsset> Related,
    IconLocation? Icon,
    IReadOnlyList<string> UnresolvedReasons);

/// Assembles everything belonging to one weapon. Resolution happens on demand: the catalogs plus
/// the single bundle holding the prefab are enough, so nothing is precomputed.
public sealed class WeaponResolver(BundleSet bundles, GameCatalogs catalogs)
{
    private readonly IconResolver _icons = new(bundles, catalogs.Lookup);

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

        // The skin materials are already listed under each skin, so they are left out here.
        var related = catalogs.Lookup.PathsForWeapon(record.WeaponNumber)
            .Where(p => p != prefabPath && !p.StartsWith("WeaponSkinsV2/WeaponSkinAssets/", StringComparison.Ordinal))
            .Select(p => new RelatedAsset(NamespaceOf(p), p, catalogs.Lookup.BundleFor(p)))
            .OrderBy(r => r.Namespace, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();

        var icon = _icons.ForWeapon(record);
        if (icon is null) unresolved.Add($"no icon texture named '{record.Slug}{IconResolver.Suffix}'");

        return new WeaponTree(
            record, displayName ?? record.Slug, prefabBundle, assets, skins, related, icon, unresolved);
    }

    private static string NamespaceOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : "(root)";
    }
}
