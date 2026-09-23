namespace PGAssetTool.Core.Settings;

/// A name from the game's data, or from an author, turned into one Windows will take.
///
/// Filed here beside `AtomicFile` because it is the same kind of thing: how this tool deals with
/// the file system rather than with the game. It was written four times before — for the files an
/// export writes, for the folders a pack is filed under, for the pack's own name and for the folder
/// a store keeps per installation — and each copy knew about a different way a name can be refused:
///
/// - A name that is **only** characters Windows will not take came back empty, and an empty name
///   is a path that quietly means the folder above it.
/// - A component ending in a **dot or a space** is not a name Windows will create. The exporter's
///   copy did not know that, and it names directories as well as files — `related/<namespace>` is
///   one of the game's own strings.
///
/// So: every character it will not take becomes an underscore, the ends are tidied, and a name left
/// with nothing becomes whatever the caller would rather see.
public static class SafeName
{
    public static string For(string name, string whenEmpty = "_")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];
        for (var at = 0; at < name.Length; at++)
            buffer[at] = invalid.Contains(name[at]) ? '_' : name[at];

        var safe = new string(buffer).Trim().TrimEnd('.', ' ');
        return safe.Length == 0 ? whenEmpty : safe;
    }
}
