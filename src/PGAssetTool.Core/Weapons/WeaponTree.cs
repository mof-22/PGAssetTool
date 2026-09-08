using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Weapons;

/// A skin, with the materials it names resolved to the bundles holding them and the textures those
/// materials use. Resolved here rather than in the tree so both the exporter and the GUI see the
/// same thing.
public sealed record WeaponSkinView(
    SkinRecord Record, string? DisplayName, IReadOnlyList<SkinMaterial> Materials, SkinModel? Model);

/// A skin that replaces the weapon rather than repainting it.
///
/// Most skins are a set of materials over the same geometry. Some bring their own model, filed
/// under CustomModels by the skin's own id — and for those, the materials the skin names do not
/// resolve against the usual roots at all, because the model carries what it needs. Recorded as a
/// place rather than as its contents: reading the closure of every skin would cost a bundle walk
/// per skin every time a weapon is selected, and it is wanted only when one is being exported.
public sealed record SkinModel(string AssetPath, string Bundle);

public sealed record SkinMaterial(string Path, string Bundle, string Name, IReadOnlyList<AssetNode> Textures)
{
    /// Where one of this material's textures actually lives.
    ///
    /// The walk names a bundle only for the textures it had to cross a file to reach; one sitting
    /// in the same file as the material is recorded with none, and the material's own is then the
    /// answer. Asked in more than one place — the tree files a texture under this name and the
    /// preview looks the same texture up again by it — and the two answers drifting apart is not
    /// something either end can see, so the rule lives here rather than at each of them.
    public (string Bundle, long PathId) Locate(AssetNode texture)
        => (texture.Bundle.Length > 0 ? texture.Bundle : Bundle, texture.PathId);
}

/// The textures a mesh is drawn with, in submesh order: entry i belongs to submesh i, and a null
/// entry is a material slot whose texture could not be found.
public sealed record MeshTextures(long MeshPathId, IReadOnlyList<AssetNode?> BySubMesh);

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
    IReadOnlyList<string> UnresolvedReasons,
    IReadOnlyList<MeshTextures> MeshTextures);

/// Assembles everything belonging to one weapon. Resolution happens on demand: the catalogs plus
/// the single bundle holding the prefab are enough, so nothing is precomputed.
public sealed class WeaponResolver(BundleSet bundles, GameCatalogs catalogs)
{
    private readonly IconResolver _icons = new(bundles, catalogs.Lookup);
    private readonly BundleGraph _graph = new(bundles);

    /// Reached by weapons, but not part of one: shared engine assets that cannot be replaced here.
    /// Not worth walking into. A shader is neither replaceable nor readable, and dumping the
    /// closure of one produces tens of megabytes of JSON for something nobody can act on.
    public static readonly IReadOnlySet<AssetClassID> Opaque = new HashSet<AssetClassID> { AssetClassID.Shader };

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
                // Filtered after the walk and never during it: what a withheld object points at is
                // found exactly as before — a texture a component names is still a texture — and it
                // is the object itself that does not appear. See Replaceable for which and why.
                assets = ReferenceWalker
                    .Closure(bundles.Context, file, root.PathId, _graph.Resolve, skip: Opaque)
                    .Where(a => Pack.Replaceable.CanShow(a.Class))
                    .ToList();
        }

        var skins = catalogs.Skins.ForWeapon(record.Index)
            .Select(s => new WeaponSkinView(
                s, catalogs.Localization.Translate(s.LocalizationKey), Materials(s), CustomModel(s)))
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
            record, displayName ?? record.Slug, prefabBundle, assets, skins, related, icon, unresolved,
            prefabBundle is null ? [] : TexturesForMeshes(prefabBundle, assets));
    }

    /// Which textures each mesh in the weapon is actually drawn with.
    ///
    /// A Mesh asset says nothing about its appearance; the renderer that draws it holds the
    /// materials, and Unity pairs submesh i with material i. A SkinnedMeshRenderer names its own
    /// mesh, while a MeshRenderer leaves that to a MeshFilter on the same GameObject, so both
    /// shapes have to be followed to cover every weapon.
    /// The same question asked of something resolved later: a skin's own model, whose closure is
    /// read when somebody opens it rather than when the weapon is selected.
    ///
    /// Public because the answer is the same one, worked out the same way. A second implementation
    /// for the skin case would be a second thing to get wrong about how Unity pairs a submesh with
    /// a material.
    public IReadOnlyList<MeshTextures> TexturesFor(string bundle, IReadOnlyList<AssetNode> assets)
        => TexturesForMeshes(bundle, assets);

    private IReadOnlyList<MeshTextures> TexturesForMeshes(string prefabBundle, IReadOnlyList<AssetNode> assets)
    {
        var found = new List<MeshTextures>();
        var meshOfGameObject = new Dictionary<long, long>();
        var renderers = new List<(long GameObject, long Mesh, AssetTypeValueField Field, string Bundle)>();

        foreach (var node in assets)
        {
            var bundle = node.Bundle.Length > 0 ? node.Bundle : prefabBundle;
            AssetsFileInstance file;
            AssetTypeValueField? field;
            try
            {
                file = bundles.Open(bundle);
                var info = file.file.GetAssetInfo(node.PathId);
                field = info is null ? null : bundles.Context.Deserialize(file, info);
            }
            catch (Exception e) when (e is IOException or FileNotFoundException) { continue; }
            if (field is null) continue;

            var owner = field["m_GameObject"];
            var on = owner.IsDummy ? 0 : owner["m_PathID"].AsLong;

            switch (node.Class)
            {
                case AssetClassID.MeshFilter:
                    meshOfGameObject[on] = field["m_Mesh"]["m_PathID"].AsLong;
                    break;
                case AssetClassID.SkinnedMeshRenderer:
                    renderers.Add((on, field["m_Mesh"]["m_PathID"].AsLong, field, bundle));
                    break;
                case AssetClassID.MeshRenderer:
                    renderers.Add((on, 0, field, bundle));
                    break;
            }
        }

        foreach (var (gameObject, named, renderer, bundle) in renderers)
        {
            var mesh = named != 0 ? named : meshOfGameObject.GetValueOrDefault(gameObject);
            if (mesh == 0 || found.Any(f => f.MeshPathId == mesh)) continue;

            var slots = renderer["m_Materials"]["Array"].Children
                .Select(m => MainTextureOf(bundle, m["m_FileID"].AsInt, m["m_PathID"].AsLong))
                .ToList();

            if (slots.Any(s => s is not null)) found.Add(new MeshTextures(mesh, slots));
        }

        return found;
    }

    /// The texture bound to a material's main slot, wherever the material and the texture live.
    private AssetNode? MainTextureOf(string from, int fileId, long pathId)
    {
        if (pathId == 0) return null;

        var (file, bundle) = fileId == 0
            ? (SafeOpen(from), from)
            : _graph.Resolve(SafeOpen(from) ?? throw new InvalidOperationException(), fileId) is { } next
                ? (next.File, next.Bundle)
                : (null, "");

        if (file is null) return null;

        var info = file.file.GetAssetInfo(pathId);
        var material = info is null ? null : bundles.Context.Deserialize(file, info);
        if (material is null) return null;

        // _MainTex first; some materials only bind another slot, and showing that beats showing
        // nothing at all.
        var slots = material["m_SavedProperties"]["m_TexEnvs"]["Array"].Children;
        var main = slots.FirstOrDefault(s => s["first"].AsString == "_MainTex") ?? slots.FirstOrDefault();
        if (main is null) return null;

        var pointer = main["second"]["m_Texture"];
        var textureId = pointer["m_PathID"].AsLong;
        if (textureId == 0) return null;

        var (textureFile, textureBundle) = pointer["m_FileID"].AsInt == 0
            ? (file, bundle)
            : _graph.Resolve(file, pointer["m_FileID"].AsInt) is { } other ? (other.File, other.Bundle) : (null, "");
        if (textureFile is null) return null;

        var textureInfo = textureFile.file.GetAssetInfo(textureId);
        if (textureInfo is null || textureInfo.TypeId != (int)AssetClassID.Texture2D) return null;

        var name = bundles.Context.Deserialize(textureFile, textureInfo)?["m_Name"].AsString ?? "";
        return new AssetNode(textureId, AssetClassID.Texture2D, name, textureBundle);
    }

    private AssetsFileInstance? SafeOpen(string bundle)
    {
        try { return bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
    }

    /// The materials a skin names, and the textures each of them uses.
    ///
    /// A skin records its materials as paths relative to one of the skin roots rather than as
    /// pointers, so they have to be looked up rather than followed. Anything that does not resolve
    /// is left out: the tree shows what can be acted on, not what is missing.
    /// The model a skin brings with it, if it brings one. Filed under the skin's own id.
    private SkinModel? CustomModel(SkinRecord skin)
    {
        var path = CustomModelRoot + skin.Id;
        return catalogs.Lookup.BundleFor(path) is { } bundle ? new SkinModel(path, bundle) : null;
    }

    public const string CustomModelRoot = "WeaponSkinsV2/CustomModels/";

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
