using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Core.Export;

public sealed record WeaponExport(
    string Directory,
    IReadOnlyList<ExportedAsset> Assets,
    IReadOnlyList<string> Skipped);

/// Writes out everything belonging to one weapon, arranged so the result is browsable: images in
/// one place, audio in another, and the object graph as a single readable document rather than a
/// file per Transform.
public sealed class WeaponExporter(BundleSet bundles)
{
    private AssetExporter? _writer;

    /// Resolves references that leave the bundle they were written in, which a skin's model does
    /// as readily as the weapon's own prefab.
    private readonly BundleGraph _graph = new(bundles);

    /// Write textures with no alpha channel; see AssetExporter for why.
    public bool Opaque { get; init; }

    private AssetExporter _exporter => _writer ??= new AssetExporter(bundles) { Opaque = Opaque };

    /// Types worth a file of their own. Everything else is scene plumbing that reads better as part
    /// of the prefab document.
    private static readonly Dictionary<AssetClassID, string> Folders = new()
    {
        [AssetClassID.Texture2D] = "textures",
        [AssetClassID.AudioClip] = "audio",
        [AssetClassID.Mesh] = "meshes",
        [AssetClassID.Material] = "materials",
        [AssetClassID.AnimationClip] = "animations",
    };

    /// One of the weapon's skins to write out as well as the default look, by id or display name.
    ///
    /// Off by default. A weapon carries up to a dozen skins and each brings its own materials,
    /// textures and sometimes a whole model; writing all of them would multiply a workspace several
    /// times over for the sake of the one an author is actually working on.
    public string? Skin { get; init; }

    public WeaponExport Export(WeaponTree tree, string outputRoot)
    {
        var chosen = Chosen(tree);
        var directory = Path.Combine(outputRoot,
            $"{tree.Record.GameNumber:D4}_{AssetExporter.Sanitize(tree.Record.Slug)}"
            + (chosen is null ? "" : $"_{AssetExporter.Sanitize(Suffix(tree, chosen))}"));
        Directory.CreateDirectory(directory);

        var assets = new List<ExportedAsset>();
        var skipped = new List<string>();

        // What the weapon itself contributes when a skin was asked for.
        //
        // Nothing, if the skin brings its own model: it replaces the weapon rather than repainting
        // it, and every one of the weapon's own textures, sounds and animations is then a file the
        // author has no reason to touch. Its geometry and nothing else, if the skin only repaints:
        // that is the thing being repainted, and the paint is the skin's own.
        var wanted = chosen is null ? Folders
            : chosen.Model is null
                ? Folders.Where(f => f.Key == AssetClassID.Mesh).ToDictionary(f => f.Key, f => f.Value)
                : [];

        if (tree.PrefabBundle is not null && wanted.Count > 0)
        {
            var closure = new List<AssetTypeValueField>();

            // A weapon spans bundles: the prefab in one, its materials and textures in another. Each
            // object is read and addressed in the bundle it actually lives in, or a pack built from
            // this workspace would name the wrong container and fail to apply.
            foreach (var group in tree.PrefabAssets.GroupBy(a => a.Bundle.Length > 0 ? a.Bundle : tree.PrefabBundle))
            {
                AssetsFileInstance file;
                try
                {
                    file = bundles.Open(group.Key);
                }
                catch (Exception ex) when (ex is IOException or FileNotFoundException)
                {
                    skipped.Add($"{group.Key}: {ex.Message}");
                    continue;
                }

                foreach (var node in group)
                {
                    var info = file.file.GetAssetInfo(node.PathId);
                    if (info is null) continue;

                    if (wanted.TryGetValue(node.Class, out var folder))
                        Once(assets, _exporter.Export(
                            group.Key, file, info, Path.Combine(directory, folder)));
                    else if (chosen is null && bundles.Context.Deserialize(file, info) is { } field)
                        closure.Add(field);
                }
            }

            if (closure.Count > 0)
            {
                var path = Path.Combine(directory, "prefab.json");
                File.WriteAllText(path, FieldDump.ToJson(closure));
                // The document covers the whole closure, so it has no single asset to address.
                assets.Add(new ExportedAsset(path, AssetClassID.GameObject, tree.Record.PrefabName,
                    "json", new FileInfo(path).Length, new AssetAddress("", "", "")));
            }
        }

        // The weapon's own shop icon, and only when the weapon is what was asked for. A skin has one
        // of its own among the related assets, and the weapon's says nothing about the skin.
        if (chosen is null)
        {
            if (tree.Icon is { AssetPath: not null } icon)
                ExportByName(icon.Container, icon.TextureName, Path.Combine(directory, "icon"), assets, skipped);
            else if (tree.Icon is not null)
                skipped.Add($"{tree.Icon.TextureName}: lives in {tree.Icon.Container}, outside the bundle cache");
        }

        foreach (var related in tree.Related)
        {
            if (!Wanted(tree, related, chosen)) continue;
            if (related.Bundle is null) { skipped.Add($"{related.Path}: bundle unknown"); continue; }
            var leaf = related.Path[(related.Path.LastIndexOf('/') + 1)..];
            ExportByName(related.Bundle, leaf,
                Path.Combine(directory, "related", AssetExporter.Sanitize(related.Namespace)), assets, skipped);
        }

        if (chosen is not null) ExportSkin(chosen, directory, assets, skipped);
        else if (Skin is { Length: > 0 } asked)
            skipped.Add($"'{asked}': this weapon has no such skin");

        DrawPackIcon(tree, directory, skipped);

        return new WeaponExport(directory, assets, skipped);
    }

    /// Records what was written, without listing the same file twice.
    ///
    /// One asset is reached by several routes — a texture four materials name, a mesh that both the
    /// weapon and a skin's model use — and following each route wrote the same file to the same
    /// path and added another row for it. What came out was right; what was listed was the same
    /// picture four times over, in the editor and in the manifest behind it.
    private static void Once(List<ExportedAsset> into, IEnumerable<ExportedAsset> written)
    {
        foreach (var asset in written)
            if (!into.Any(a => string.Equals(a.Path, asset.Path, StringComparison.OrdinalIgnoreCase)))
                into.Add(asset);
    }

    /// Whether a related asset belongs to what is being written out.
    ///
    /// Everything the lookup table names for this weapon lands in Related, and most of it belongs
    /// to one skin: an offer icon, a profile animation and an info record per skin, plus each
    /// skin's own definition. Writing the lot meant that extracting one weapon produced eight
    /// offer icons and that asking for one skin produced the other seven as well — files an author
    /// has no business changing to change the thing they asked for.
    ///
    /// Ownership is read off the name. A skin's assets are named after the skin's own id, so an
    /// asset whose name begins with one is that skin's and an asset whose name begins with none of
    /// them is the weapon's.
    private static bool Wanted(WeaponTree tree, RelatedAsset related, WeaponSkinView? chosen)
    {
        // A skin's own model, which the skin export walks properly — everything it reaches, in the
        // folders its types belong in. Taken by name here instead it came out as whatever else in
        // that bundle happened to share the name, which for these was the skin's texture.
        if (related.Path.StartsWith("WeaponSkinsV2/CustomModels/", StringComparison.OrdinalIgnoreCase))
            return false;

        var leaf = related.Path[(related.Path.LastIndexOf('/') + 1)..];
        var owner = tree.Skins
            .Select(s => s.Record.Id)
            .Where(id => Named(leaf, id))
            .OrderByDescending(id => id.Length)   // Weapon834_snow_night over Weapon834_snow
            .FirstOrDefault();

        // The default skin is the weapon as it comes, so what is filed under it is the weapon's.
        var weaponsOwn = owner is null
            || owner.EndsWith("_default", StringComparison.OrdinalIgnoreCase);

        return chosen is null
            ? weaponsOwn
            : Named(leaf, chosen.Record.Id);
    }

    /// Whether `leaf` is `id` itself or something filed under it, rather than a longer name that
    /// merely starts with the same letters.
    private static bool Named(string leaf, string id)
        => leaf.StartsWith(id, StringComparison.OrdinalIgnoreCase)
            && (leaf.Length == id.Length || leaf[id.Length] == '_');

    /// The skin that was asked for, matched on its id or on the name a player would see.
    private WeaponSkinView? Chosen(WeaponTree tree)
        => Skin is not { Length: > 0 } asked
            ? null
            : tree.Skins.FirstOrDefault(s =>
                string.Equals(s.Record.Id, asked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.DisplayName, asked, StringComparison.OrdinalIgnoreCase));

    /// What the workspace directory is called after the weapon's own name.
    ///
    /// The skin's id already begins with the prefab name — Weapon834_christmas — so the whole of it
    /// would read as the weapon twice. What is left is what tells the skins apart.
    private static string Suffix(WeaponTree tree, WeaponSkinView skin)
        => skin.Record.Id.StartsWith(tree.Record.PrefabName + "_", StringComparison.OrdinalIgnoreCase)
            ? skin.Record.Id[(tree.Record.PrefabName.Length + 1)..]
            : skin.Record.Id;

    /// Writes a skin's own materials and textures, and the model it brings if it brings one.
    ///
    /// Kept apart from the weapon's own files. The two overlap — a skin repaints the same geometry
    /// — and mixing them would leave an author unable to tell which texture belonged to the skin
    /// they meant to change.
    private void ExportSkin(
        WeaponSkinView skin, string directory, List<ExportedAsset> assets, List<string> skipped)
    {
        var into = Path.Combine(directory, "skin");

        foreach (var material in skin.Materials)
        {
            ExportByName(material.Bundle, material.Name, Path.Combine(into, "materials"),
                assets, skipped, AssetClassID.Material);

            foreach (var texture in material.Textures)
                ExportByName(
                    texture.Bundle.Length > 0 ? texture.Bundle : material.Bundle,
                    texture.Name, Path.Combine(into, "textures"), assets, skipped, AssetClassID.Texture2D);
        }

        if (skin.Model is not { } model) return;

        // The model is a prefab like the weapon's own, so it is walked the same way: everything it
        // reaches, written into the folders its types belong in.
        var name = model.AssetPath[(model.AssetPath.LastIndexOf('/') + 1)..];
        AssetsFileInstance file;
        try { file = bundles.Open(model.Bundle); }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            skipped.Add($"{name}: {ex.Message}");
            return;
        }

        var root = ReferenceWalker.FindByName(bundles.Context, file, AssetClassID.GameObject, name);
        if (root is null) { skipped.Add($"{name}: no such model in '{model.Bundle}'"); return; }

        var closure = new List<AssetTypeValueField>();
        foreach (var node in ReferenceWalker.Closure(
                     bundles.Context, file, root.PathId, _graph.Resolve, skip: WeaponResolver.Opaque))
        {
            var bundle = node.Bundle.Length > 0 ? node.Bundle : model.Bundle;
            AssetsFileInstance holder;
            try { holder = bundles.Open(bundle); }
            catch (Exception ex) when (ex is IOException or FileNotFoundException) { continue; }

            var info = holder.file.GetAssetInfo(node.PathId);
            if (info is null) continue;

            if (Folders.TryGetValue(node.Class, out var folder))
                Once(assets, _exporter.Export(bundle, holder, info, Path.Combine(into, folder)));
            else if (bundles.Context.Deserialize(holder, info) is { } field)
                closure.Add(field);
        }

        if (closure.Count == 0) return;

        var path = Path.Combine(into, "model.json");
        File.WriteAllText(path, FieldDump.ToJson(closure));
        assets.Add(new ExportedAsset(path, AssetClassID.GameObject, name,
            "json", new FileInfo(path).Length, new AssetAddress("", "", "")));
    }

    /// Draws the weapon and leaves the picture in the workspace, for the pack to show itself with.
    ///
    /// The game's own icon says which weapon a pack is for and nothing about what the pack does to
    /// it, which for a texture or mesh mod is the only interesting part. This is the vanilla model,
    /// since nothing has been edited yet at this point — the editor can draw it again from whatever
    /// angle the author likes once it has.
    private void DrawPackIcon(WeaponTree tree, string directory, List<string> skipped)
    {
        try
        {
            // The biggest mesh that has textures: a weapon carries arms and muzzle-flash geometry
            // as well, and neither of those is what anybody means by the weapon.
            var subject = tree.MeshTextures
                .Select(slots => (Slots: slots, Mesh: ReadMesh(tree, slots.MeshPathId)))
                .Where(m => m.Mesh is not null)
                .MaxBy(m => m.Mesh!.VertexCount);

            if (subject.Mesh is null) return;

            var textures = subject.Slots.BySubMesh.Select(TextureFor).ToList();
            var picture = Preview.PackIcon.Render(subject.Mesh, textures);
            if (Preview.PackIcon.IsBlank(picture)) return;

            Preview.PackIcon.Write(picture, Path.Combine(directory, Preview.PackIcon.FileName));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException)
        {
            // A pack without a picture is a small loss; an export that failed over one is not.
            skipped.Add($"{Preview.PackIcon.FileName}: {ex.Message}");
        }
    }

    private UnityMesh? ReadMesh(WeaponTree tree, long pathId)
    {
        var node = tree.PrefabAssets.FirstOrDefault(a => a.PathId == pathId);
        if (node is null) return null;

        var bundle = node.Bundle.Length > 0 ? node.Bundle : tree.PrefabBundle;
        if (bundle is null) return null;

        var file = bundles.Open(bundle);
        var info = file.file.AssetInfos.FirstOrDefault(i => i.PathId == pathId);
        if (info is null) return null;

        var field = bundles.Context.Deserialize(file, info);
        return field is null ? null : Preview.AssetPreview.Mesh(field);
    }

    private Preview.PreviewImage? TextureFor(Assets.AssetNode? node)
    {
        if (node is null || node.Bundle.Length == 0) return null;

        try
        {
            if (Preview.AssetPreview.Locate(bundles, node.Bundle, AssetClassID.Texture2D, node.PathId, node.Name)
                is not var (file, info)) return null;

            var field = bundles.Context.Deserialize(file, info);
            return field is null ? null : Preview.AssetPreview.Texture(bundles, node.Bundle, field);
        }
        catch (Exception e) when (e is IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// Exports, then writes a pack manifest naming every replaceable file. Editing a file and
    /// running pack turns it into a mod; untouched files are left out on their own.
    public WeaponExport ExportAsWorkspace(WeaponTree tree, string outputRoot, string author, string? gameVersion)
    {
        var export = Export(tree, outputRoot);
        var chosen = Chosen(tree);

        Pack.Workspace.Create(
            export.Directory,
            id: Identity(tree, chosen),
            name: chosen is null ? tree.DisplayName : $"{tree.DisplayName} — {chosen.DisplayName ?? chosen.Record.Id}",
            author: author,
            gameVersion: gameVersion,
            assets: export.Assets,
            subject: new Pack.PackSubject
            {
                Kind = Pack.PackKind.Weapon,
                Id = AssetExporter.Sanitize(tree.Record.Slug).ToLowerInvariant(),
                Number = tree.Record.GameNumber,
                Prefab = tree.Record.PrefabName,
                Name = tree.DisplayName,
                Variant = chosen?.Record.Id ?? "",
                VariantName = chosen?.DisplayName ?? "",
                NameKey = tree.Record.LocalizationKey ?? "",
                VariantKey = chosen?.Record.LocalizationKey ?? "",
            });
        return export;
    }

    /// What the tool will know this mod by, for as long as it exists.
    ///
    /// A name and a number would not do it. The id was the weapon's slug alone, so every mod of a
    /// weapon was the same mod: installing a skin replaced the plain one, and installing somebody
    /// else's replaced yours — silently, because replacing by id is exactly how a mod is updated.
    /// Two mods of one weapon are two mods, whoever made them and whichever look they change.
    ///
    /// So each extraction mints its own. The readable half says what it is for at a glance; the
    /// last eight characters are what make it this one. It is written into the workspace once and
    /// never changes after — rebuilding the same workspace updates the mod it built before, which
    /// is the one case where replacing is what was meant.
    private static string Identity(WeaponTree tree, WeaponSkinView? chosen)
    {
        var slug = AssetExporter.Sanitize(tree.Record.Slug).ToLowerInvariant();
        var look = chosen is null
            ? "default"
            : AssetExporter.Sanitize(Suffix(tree, chosen)).ToLowerInvariant();
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

        return $"{slug}-{look}-{token}";
    }

    /// <param name="only">
    /// Which class to take, when the name alone is not enough. A skin's material and the texture it
    /// paints with are routinely called the same thing, and without this the material was written
    /// into the textures folder as well as its own.
    /// </param>
    private void ExportByName(
        string bundle, string name, string directory, List<ExportedAsset> into, List<string> skipped,
        AssetClassID? only = null)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception ex) { skipped.Add($"{name}: {ex.Message}"); return; }

        var matches = file.file.AssetInfos
            .Where(i => only is null || i.TypeId == (int)only)
            .Where(i => NameOf(file, i) is { } n && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) { skipped.Add($"{name}: not found in '{bundle}'"); return; }
        foreach (var info in matches) Once(into, _exporter.Export(bundle, file, info, directory));
    }

    private string? NameOf(AssetsFileInstance file, AssetFileInfo info)
    {
        var name = bundles.Context.Deserialize(file, info)?["m_Name"];
        return name is null || name.IsDummy ? null : name.AsString;
    }
}
