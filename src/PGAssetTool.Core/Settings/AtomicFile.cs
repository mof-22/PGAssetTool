namespace PGAssetTool.Core.Settings;

/// Writes a file in a way that cannot leave half of one behind.
///
/// `File.WriteAllText` empties the file and then fills it, so anything that stops the tool in
/// between — a crash, the machine going down, the disk filling — leaves a truncated file where a
/// whole one was. For the mod ledger that is the record of which mod put what where, and the tool
/// reads it on every start; for the settings it is the reason a session comes up with none of the
/// author's preferences.
///
/// So the new contents go to a file beside it and are put in place with a rename, which Windows
/// does in one step: afterwards the file is either wholly the old contents or wholly the new ones.
/// The bytes are pushed to the disk before the rename, because a rename that lands while the
/// contents are still in the system's cache would be the same problem one layer down.
public static class AtomicFile
{
    /// The half-written name. Nothing reads it, and a run that dies between the write and the
    /// rename leaves one for the next write to overwrite.
    private const string Unfinished = ".writing";

    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    /// The same for a copy: the pack the mod store keeps is what reinstalling and removing go
    /// through, and half of one there is worse than none — the ledger points at it either way.
    public static void Copy(string from, string to)
    {
        var writing = to + Unfinished;
        File.Copy(from, writing, overwrite: true);
        File.Move(writing, to, overwrite: true);
    }

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var writing = path + Unfinished;
        using (var stream = new FileStream(writing, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(writing, path, overwrite: true);
    }
}
