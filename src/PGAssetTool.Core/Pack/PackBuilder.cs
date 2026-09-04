using System.IO.Compression;

namespace PGAssetTool.Core.Pack;

public sealed record PackResult(string Path, int Operations, long Bytes, IReadOnlyList<string> Unchanged);

/// Builds a .pgmod: a zip holding the manifest and the files it references, and nothing else.
public static class PackBuilder
{
    public const string Extension = ".pgmod";

    public static PackResult Build(string workspace, string outputPath)
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

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        if (File.Exists(outputPath)) File.Delete(outputPath);

        using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(PackManifest.FileName).Open()))
                writer.Write(packed.ToJson());

            foreach (var source in packed.Operations.Select(o => o.Source).Distinct())
                archive.CreateEntryFromFile(Path.Combine(workspace, source), source);
        }

        return new PackResult(outputPath, packed.Operations.Count, new FileInfo(outputPath).Length, unchanged);
    }

    public static PackManifest ReadManifest(string packPath)
    {
        using var archive = ZipFile.OpenRead(packPath);
        var entry = archive.GetEntry(PackManifest.FileName)
            ?? throw new InvalidDataException($"'{packPath}' has no {PackManifest.FileName}.");
        using var reader = new StreamReader(entry.Open());
        return PackManifest.Parse(reader.ReadToEnd());
    }
}
