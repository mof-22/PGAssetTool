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
            if (Replaceable.OperationForFormat(asset.Format) is not { } op) continue;
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

    /// Writes the manifest back, keeping the operations exactly as they were.
    ///
    /// Only the descriptive half is ever edited by hand — what the mod is called, who wrote it,
    /// which version it is. The operations are the addresses the export resolved, and nothing that
    /// edits a name has any business rewriting those.
    public static void Save(string directory, PackManifest manifest)
        => File.WriteAllText(Path.Combine(directory, PackManifest.FileName), manifest.ToJson());

    /// Gives the workspace directory a different name, in place, and answers where it went.
    ///
    /// The directory name is what the built pack is called, so this is how an author decides what
    /// their mod's file is named rather than living with the number and prefab the export chose.
    /// Nothing inside the workspace refers to the directory by name — operations are relative — so
    /// there is nothing to rewrite afterwards.
    public static string Rename(string directory, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A workspace needs a name.", nameof(name));
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{trimmed}' cannot be a folder name.", nameof(name));

        var from = Path.GetFullPath(Path.TrimEndingDirectorySeparator(directory));
        var to = Path.Combine(Path.GetDirectoryName(from)!, trimmed);
        if (from == to) return from;

        // Changing only the capitalisation is a real rename, and the directory it "already exists"
        // as is the one being renamed — so that check has to let this one case through.
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && Directory.Exists(to))
            throw new IOException($"There is already a workspace called '{trimmed}'.");

        Directory.Move(from, to);
        return to;
    }

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
