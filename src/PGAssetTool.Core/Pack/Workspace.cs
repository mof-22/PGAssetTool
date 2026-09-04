using System.Security.Cryptography;
using PGAssetTool.Core.Export;

namespace PGAssetTool.Core.Pack;

/// The directory an author works in: the exported files plus a manifest that already names the
/// target of every replaceable one.
///
/// Each operation records the hash of its file as exported. Packing keeps only the operations whose
/// file has since changed, so an author edits the two images they care about and the pack contains
/// those two, without having to prune anything by hand.
public static class Workspace
{
    /// Only types with a working import path get an operation. Listing types that cannot yet be
    /// applied would produce packs that fail at apply time.
    private static readonly Dictionary<string, string> OperationForFormat = new()
    {
        ["png"] = PackOperations.ReplaceTexture,
    };

    /// `alreadyModified` marks a workspace whose files are the modification rather than a starting
    /// point — the output of converting someone's existing mod, say. Those carry no baseline, so
    /// packing takes them as they are instead of waiting for an edit that already happened.
    public static PackManifest Create(
        string directory, string id, string name, string author, string? gameVersion,
        IEnumerable<ExportedAsset> assets, bool alreadyModified = false)
    {
        var operations = new List<PackOperation>();
        foreach (var asset in assets)
        {
            if (!OperationForFormat.TryGetValue(asset.Format, out var op)) continue;
            if (asset.Address.Container.Length == 0) continue;

            operations.Add(new PackOperation
            {
                Op = op,
                Target = asset.Address,
                Source = Relative(directory, asset.Path),
                BaselineSha256 = alreadyModified ? null : HashFile(asset.Path),
            });
        }

        var manifest = new PackManifest
        {
            Id = id,
            Name = name,
            Author = author,
            BuiltAgainstGameVersion = gameVersion,
            Operations = operations,
        };
        File.WriteAllText(Path.Combine(directory, PackManifest.FileName), manifest.ToJson());
        return manifest;
    }

    public static PackManifest Read(string directory)
        => PackManifest.Parse(File.ReadAllText(Path.Combine(directory, PackManifest.FileName)));

    /// The operations whose source file no longer matches what was exported.
    public static List<PackOperation> Changed(string directory, PackManifest manifest)
    {
        var changed = new List<PackOperation>();
        foreach (var operation in manifest.Operations)
        {
            var path = Path.Combine(directory, operation.Source);
            if (!File.Exists(path)) continue;
            if (operation.BaselineSha256 is { } baseline && HashFile(path) == baseline) continue;
            changed.Add(operation);
        }
        return changed;
    }

    public static string HashFile(string path)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Relative(string directory, string path)
        => Path.GetRelativePath(directory, path).Replace('\\', '/');
}
