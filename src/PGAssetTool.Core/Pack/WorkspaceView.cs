using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Pack;

/// One replaceable file in a workspace, and whether it has been touched since it was written out.
public sealed record WorkspaceFile(
    string RelativePath, string FullPath, AssetAddress Target, string Operation, bool Edited, long Bytes)
{
    public string Folder
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? "" : RelativePath[..slash];
        }
    }

    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
}

/// A directory an author is working in, as it stands right now.
///
/// Read fresh each time rather than watched into a model: the files are edited by other programs,
/// and the only trustworthy answer to what has changed is the one taken at the moment of asking.
public sealed record WorkspaceView(string Directory, PackManifest Manifest, IReadOnlyList<WorkspaceFile> Files)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
    public int EditedCount => Files.Count(f => f.Edited);

    /// Every directory under `root` that holds a manifest, most recently written first.
    ///
    /// Ordered by the manifest rather than the directory: a directory's timestamp only moves when
    /// its own entries change, so writing files into subfolders leaves it looking untouched.
    public static IReadOnlyList<string> Discover(string root)
    {
        if (!System.IO.Directory.Exists(root)) return [];

        return System.IO.Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, PackManifest.FileName)))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, PackManifest.FileName)))
            .ToList();
    }

    public static WorkspaceView? Open(string directory)
    {
        PackManifest manifest;
        try { manifest = Workspace.Read(directory); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }

        var changed = Workspace.Changed(directory, manifest).ToHashSet();

        var files = manifest.Operations.Select(operation =>
        {
            var full = Path.Combine(directory, operation.Source);
            var info = new FileInfo(full);
            return new WorkspaceFile(
                operation.Source, full, operation.Target, operation.Op,
                changed.Contains(operation), info.Exists ? info.Length : 0);
        })
        .OrderBy(f => f.Folder, StringComparer.Ordinal)
        .ThenBy(f => f.Name, StringComparer.Ordinal)
        .ToList();

        return new WorkspaceView(directory, manifest, files);
    }
}
