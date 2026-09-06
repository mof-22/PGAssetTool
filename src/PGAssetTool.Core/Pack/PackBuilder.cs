using System.IO.Compression;

namespace PGAssetTool.Core.Pack;

public sealed record PackResult(string Path, int Operations, long Bytes, IReadOnlyList<string> Unchanged);

/// Builds a .pgmod: a zip holding the manifest and the files it references, and nothing else.
public static class PackBuilder
{
    public const string Extension = ".pgmod";

    /// What the built file is called: the mod's name, and nothing else.
    ///
    /// It used to be the workspace directory's name, which made three names for one thing — the
    /// folder, the manifest's name, and the id — each authoritative somewhere different. The
    /// manager showed one, the file on disk carried another, and renaming a working directory
    /// quietly renamed what an author was about to hand out.
    ///
    /// The folder is where somebody works. The name is what the mod is called, wherever it is
    /// written down. The id is what the ledger matches an update by, and is nobody's business but
    /// the tool's.
    public static string FileNameFor(PackManifest manifest)
    {
        var name = Sanitise(manifest.Name);
        if (name.Length == 0) name = Sanitise(manifest.Id);
        if (name.Length == 0) name = "mod";
        return name + Extension;
    }

    /// Where a pack built from this workspace lands by default: beside the files it came from.
    public static string OutputFor(string workspace, PackManifest manifest)
        => Path.Combine(workspace, FileNameFor(manifest));

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        // Windows will not have a name ending in a dot, and a name that is only dots and spaces
        // leaves nothing behind at all.
        return clean.TrimEnd('.', ' ');
    }

    /// <param name="signer">
    /// Signs and scrambles the pack when given. Null leaves it a plain zip, which is what every
    /// pack was before this and what a pack built with protection off still is.
    /// </param>
    public static PackResult Build(string workspace, string outputPath, PackAuthor? signer = null)
    {
        var manifest = Workspace.Read(workspace);
        var changed = Workspace.Changed(workspace, manifest);
        var unchanged = manifest.Operations
            .Where(o => !changed.Contains(o))
            .Select(o => o.Source)
            .ToList();

        if (changed.Count == 0)
            throw new InvalidOperationException(
                $"Nothing in '{workspace}' differs from what was exported, so there is no change to pack.");

        // The baseline hash is a working-directory concern; it says nothing to whoever applies the pack.
        var packed = manifest with { Operations = changed.Select(o => o with { BaselineSha256 = null }).ToList() };

        // The icon goes in whether or not it is one of the files being replaced, and drops out of
        // the manifest if it is not there to go in — a pack claiming a picture it does not carry
        // would be a broken pack rather than one without a picture.
        var icon = packed.Icon.Length > 0 && File.Exists(Path.Combine(workspace, packed.Icon))
            ? packed.Icon
            : "";
        packed = packed with { Icon = icon };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        // Built in memory rather than straight to the file: a protected pack is this zip with a
        // header in front of it and a keystream over it, and it has to be signed as a whole before
        // any of it reaches disk.
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(PackManifest.FileName).Open()))
                writer.Write(packed.ToJson());

            var files = packed.Operations.Select(o => o.Source);
            if (icon.Length > 0) files = files.Append(icon);

            foreach (var source in files.Distinct(StringComparer.OrdinalIgnoreCase))
                archive.CreateEntryFromFile(Path.Combine(workspace, source), source);
        }

        PackFile.Write(outputPath, bytes.ToArray(), signer, packed.Author);

        return new PackResult(outputPath, packed.Operations.Count, new FileInfo(outputPath).Length, unchanged);
    }

    /// The pack's picture, as the bytes of whatever image it carries. Null when it has none.
    ///
    /// Read from the pack each time rather than unpacked to somewhere: a manager listing a dozen
    /// mods reads a dozen small images once, and keeping copies of them would mean a second place
    /// that can disagree with the pack about what the pack looks like.
    public static byte[]? ReadIcon(string packPath)
    {
        try
        {
            using var archive = PackFile.Open(packPath);
            var manifest = ReadManifest(archive);
            if (manifest.Icon.Length == 0) return null;

            if (archive.GetEntry(manifest.Icon) is not { } entry) return null;
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static PackManifest ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(PackManifest.FileName)
            ?? throw new InvalidDataException($"the pack has no {PackManifest.FileName}.");
        using var reader = new StreamReader(entry.Open());
        return PackManifest.Parse(reader.ReadToEnd());
    }

    public static PackManifest ReadManifest(string packPath)
    {
        using var archive = PackFile.Open(packPath);
        var entry = archive.GetEntry(PackManifest.FileName)
            ?? throw new InvalidDataException($"'{packPath}' has no {PackManifest.FileName}.");
        using var reader = new StreamReader(entry.Open());
        return PackManifest.Parse(reader.ReadToEnd());
    }
}
