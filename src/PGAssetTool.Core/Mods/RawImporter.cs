using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Mods;

/// Writes an asset back from its own serialized bytes.
///
/// The one import path that does not understand what it is importing, and the only one that can
/// therefore work for a Material, a Font, a Transform or anything else the tool has no editor for.
/// What it does instead is check: the bytes are parsed through the type tree of the file they are
/// going into, and written back out, and the two have to agree. That is what catches a .dat built
/// against a different version of the game — bytes that mean one thing there and another here
/// would otherwise be written in and go wrong at run time, where nothing could trace them back.
public static class RawImporter
{
    public static string Replace(
        BundleEditor editor, AssetFileInfo info, string sourcePath,
        PackOperation operation, IReadOnlyDictionary<string, long> newIds)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var cls = (AssetClassID)info.TypeId;

        // Staged before parsing, so the parse reads the incoming bytes rather than the asset they
        // are replacing. Undone if anything below refuses them.
        var previous = info.Replacer;
        info.SetNewData(bytes);

        try
        {
            var field = editor.Read(info)
                ?? throw new InvalidDataException(
                    $"the bytes in '{Path.GetFileName(sourcePath)}' do not read as a {cls}.");

            var written = field.WriteToByteArray();
            if (written.Length != bytes.Length || !written.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException(
                    $"'{Path.GetFileName(sourcePath)}' does not read back as the {cls} it claims to "
                    + "be. It was built against a different version of the game.");

            var repointed = Repoint(field, operation, newIds);
            if (repointed > 0) editor.Stage(info, field);

            return $"{bytes.Length:N0} bytes"
                + (repointed > 0 ? $", {repointed} pointer(s) repointed" : "");
        }
        catch
        {
            info.Replacer = previous;
            throw;
        }
    }

    /// Fills in the pointers that name an added asset, and answers how many were changed.
    ///
    /// A pointer whose recorded place is not there any more is a refusal rather than a warning. The
    /// alternative is an asset written in with a pointer at whatever the author's editor happened
    /// to number the new asset — which in the player's bundle is either nothing at all or, worse,
    /// something else entirely.
    public static int Repoint(
        AssetTypeValueField field, PackOperation operation, IReadOnlyDictionary<string, long> newIds)
    {
        var changed = 0;
        foreach (var fixup in operation.Pointers)
        {
            if (!newIds.TryGetValue(fixup.NewId, out var pathId))
                throw new InvalidDataException(
                    $"its {fixup.Path} points at '{fixup.NewId}', which the pack did not add.");

            var pointer = PointerPath.Resolve(field, fixup.Path)
                ?? throw new InvalidDataException(
                    $"it has no pointer at '{fixup.Path}' to point at '{fixup.NewId}'.");

            if (pointer["m_FileID"].AsInt != 0)
                throw new InvalidDataException(
                    $"its {fixup.Path} refers to another file, so it cannot be pointed at "
                    + $"'{fixup.NewId}' in this one.");

            if (pointer["m_PathID"].AsLong == pathId) continue;
            pointer["m_PathID"].AsLong = pathId;
            changed++;
        }
        return changed;
    }
}
