using System.Text.RegularExpressions;

namespace PGAssetTool.Core.RawAssets;

/// A raw asset exported by an asset editor, identified from its file name.
///
/// The convention is `&lt;name&gt;-&lt;CAB&gt;-&lt;pathId&gt;.dat`, which is enough to find the asset it came from
/// in the player's own game files. That matters because the file itself carries no type information:
/// the class has to be recovered from the original before the bytes mean anything.
public sealed partial record RawAssetFile(string Path, string Name, string Cab, long PathId)
{
    // The path id is signed, so the separator before it is followed by an optional minus.
    [GeneratedRegex(@"^(?<name>.+)-(?<cab>CAB-[0-9a-fA-F]+)-(?<pathId>-?\d+)$")]
    private static partial Regex FileName { get; }

    public static bool TryParse(string path, out RawAssetFile file)
    {
        file = null!;
        var match = FileName.Match(System.IO.Path.GetFileNameWithoutExtension(path));
        if (!match.Success) return false;
        if (!long.TryParse(match.Groups["pathId"].ValueSpan, out var pathId)) return false;

        file = new RawAssetFile(path, match.Groups["name"].Value, match.Groups["cab"].Value, pathId);
        return true;
    }

    public static IEnumerable<RawAssetFile> Discover(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.dat", SearchOption.AllDirectories)
            : [path];

        foreach (var candidate in files)
            if (TryParse(candidate, out var parsed)) yield return parsed;
    }
}
