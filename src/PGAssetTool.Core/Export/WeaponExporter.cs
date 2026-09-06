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

    public WeaponExport Export(WeaponTree tree, string outputRoot)
    {
        var directory = Path.Combine(outputRoot,
            $"{tree.Record.GameNumber:D4}_{AssetExporter.Sanitize(tree.Record.Slug)}");
        Directory.CreateDirectory(directory);

        var assets = new List<ExportedAsset>();
        var skipped = new List<string>();

        if (tree.PrefabBundle is not null)
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

                    if (Folders.TryGetValue(node.Class, out var folder))
                        assets.AddRange(_exporter.Export(
                            group.Key, file, info, Path.Combine(directory, folder)));
                    else if (bundles.Context.Deserialize(file, info) is { } field)
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

        if (tree.Icon is { AssetPath: not null } icon)
            ExportByName(icon.Container, icon.TextureName, Path.Combine(directory, "icon"), assets, skipped);
        else if (tree.Icon is not null)
            skipped.Add($"{tree.Icon.TextureName}: lives in {tree.Icon.Container}, outside the bundle cache");

        foreach (var related in tree.Related)
        {
            if (related.Bundle is null) { skipped.Add($"{related.Path}: bundle unknown"); continue; }
            var leaf = related.Path[(related.Path.LastIndexOf('/') + 1)..];
            ExportByName(related.Bundle, leaf,
                Path.Combine(directory, "related", AssetExporter.Sanitize(related.Namespace)), assets, skipped);
        }

        DrawPackIcon(tree, directory, skipped);

        return new WeaponExport(directory, assets, skipped);
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
        Pack.Workspace.Create(
            export.Directory,
            id: $"{AssetExporter.Sanitize(tree.Record.Slug).ToLowerInvariant()}",
            name: tree.DisplayName,
            author: author,
            gameVersion: gameVersion,
            assets: export.Assets);
        return export;
    }

    private void ExportByName(
        string bundle, string name, string directory, List<ExportedAsset> into, List<string> skipped)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception ex) { skipped.Add($"{name}: {ex.Message}"); return; }

        var matches = file.file.AssetInfos
            .Where(i => NameOf(file, i) is { } n && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) { skipped.Add($"{name}: not found in '{bundle}'"); return; }
        foreach (var info in matches) into.AddRange(_exporter.Export(bundle, file, info, directory));
    }

    private string? NameOf(AssetsFileInstance file, AssetFileInfo info)
    {
        var name = bundles.Context.Deserialize(file, info)?["m_Name"];
        return name is null || name.IsDummy ? null : name.AsString;
    }
}
