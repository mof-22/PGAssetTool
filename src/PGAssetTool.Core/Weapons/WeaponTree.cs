using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Weapons;

/// A skin, with the materials it names resolved to the bundles holding them and the textures those
/// materials use. Resolved here rather than in the tree so both the exporter and the GUI see the
/// same thing.
public sealed record WeaponSkinView(
    SkinRecord Record, string? DisplayName, IReadOnlyList<SkinMaterial> Materials);

public sealed record SkinMaterial(string Path, string Bundle, string Name, IReadOnlyList<AssetNode> Textures);

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
    private readonly BundleGraph _graph = new(bundles);

    /// Reached by weapons, but not part of one: shared engine assets that cannot be replaced here.
    private static readonly HashSet<AssetClassID> Opaque = [AssetClassID.Shader];

    public WeaponTree Resolve(WeaponRecord record)
    {
        var unresolved = new List<string>();

        // A hidden weapon has no localization key at all, so a missing name is expected rather than
        // a gap worth reporting.
        var displayName = catalogs.Localization.Translate(record.LocalizationKey);
        if (displayName is null && !record.IsHidden)
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
                assets = ReferenceWalker.Closure(
                    bundles.Context, file, root.PathId, _graph.Resolve, skip: Opaque);
        }

        var skins = catalogs.Skins.ForWeapon(record.Index)
            .Select(s => new WeaponSkinView(s, catalogs.Localization.Translate(s.LocalizationKey), Materials(s)))
            .ToList();

        // The skin materials are already listed under each skin, so they are left out here.
        var related = catalogs.Lookup.PathsForWeapon(record.PrefabNumber)
            .Where(p => p != prefabPath && !p.StartsWith("WeaponSkinsV2/WeaponSkinAssets/", StringComparison.Ordinal))
            .Select(p => new RelatedAsset(NamespaceOf(p), p, catalogs.Lookup.BundleFor(p)))
            .OrderBy(r => r.Namespace, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();

        var icon = _icons.ForWeapon(record);
        if (icon is null && !record.IsHidden)
            unresolved.Add($"no icon texture named '{record.Slug}{IconResolver.Suffix}'");

        return new WeaponTree(
            record, displayName ?? record.Slug, prefabBundle, assets, skins, related, icon, unresolved);
    }

    /// The materials a skin names, and the textures each of them uses.
    ///
    /// A skin records its materials as paths relative to one of the skin roots rather than as
    /// pointers, so they have to be looked up rather than followed. Anything that does not resolve
    /// is left out: the tree shows what can be acted on, not what is missing.
    private IReadOnlyList<SkinMaterial> Materials(SkinRecord skin)
    {
        var materials = new List<SkinMaterial>();

        foreach (var path in skin.MaterialPaths)
        {
            if (catalogs.Lookup.Resolve(path, AssetLookup.SkinAssetRoots) is not var (full, bundle)) continue;

            var name = full[(full.LastIndexOf('/') + 1)..];
            AssetsFileInstance file;
            try { file = bundles.Open(bundle); }
            catch (Exception e) when (e is IOException or FileNotFoundException) { continue; }

            var info = ReferenceWalker.FindByName(
                bundles.Context, file, AssetClassID.Material, name, StringComparison.OrdinalIgnoreCase);
            if (info is null) continue;

            // Only the textures, not the whole closure: a material also reaches its shader, and a
            // shader is neither replaceable nor worth a row.
            var textures = ReferenceWalker
                .Closure(bundles.Context, file, info.PathId, _graph.Resolve, skip: Opaque)
                .Where(n => n.Class == AssetClassID.Texture2D)
                .ToList();

            materials.Add(new SkinMaterial(full, bundle, name, textures));
        }

        return materials;
    }

    private static string NamespaceOf(string path)
    {
        var slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : "(root)";
    }
}
