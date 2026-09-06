using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.RawAssets;

/// Works out what class a raw .dat holds, when the asset it came from is not there to ask.
///
/// A .dat carries no type of its own. For a replacement that costs nothing — the asset at that path
/// id says what it is — but an addition has no such asset, and the bytes on their own are just
/// bytes. So a type is tried by parsing the bytes through it, writing the parsed object back, and
/// keeping the type only if the bytes come out the same. Parsing alone is a weak test: a wrong type
/// will happily read a shorter prefix and stop. Parsing and reproducing the whole file is not.
///
/// Trying every type costs about forty-five seconds, and almost none of that is the parse that
/// succeeds — a correct one takes four milliseconds. A wrong type reads a length field as an array
/// count, and then spends seconds building millions of elements before it runs off the end of the
/// data. Nothing here can shorten that, so the useful thing is not to do it: the file name from
/// every editor that writes these begins with the class, and checking that one guess first answers
/// the question immediately in the case that actually occurs. The exhaustive scan stays for when
/// there is no guess, or the guess is wrong.
public static class ClassInference
{
    /// A path id nothing uses, to hang the trial data off. The info is never added to the file, so
    /// the value only has to be distinct from every real one for the length of a parse.
    private const long Scratch = long.MinValue + 1;

    public sealed record Result(IReadOnlyList<AssetClassID> Candidates, bool FromName)
    {
        public bool IsCertain => Candidates.Count == 1;
    }

    /// <param name="hint">
    /// A file name to read a class out of, if it starts with one. Editors name a dump after the
    /// asset, and an asset with no name of its own is named after its class — `Shader #-2415…`,
    /// `TextMesh #8580…` — so this is usually the answer for exactly the assets that need one.
    /// </param>
    public static Result Infer(
        AssetsContext context, AssetsFileInstance file, byte[] bytes, string? hint = null)
    {
        if (ClassIn(hint) is { } guess && AssetAddressableIn(file, guess) && Reads(context, file, guess, bytes))
            return new Result([guess], FromName: true);

        return new Result(Candidates(context, file, bytes), FromName: false);
    }

    /// Every type the file describes that these bytes read back as. More than one is possible for a
    /// small asset, and a caller handed two should say so rather than pick.
    public static IReadOnlyList<AssetClassID> Candidates(
        AssetsContext context, AssetsFileInstance file, byte[] bytes)
    {
        var found = new List<AssetClassID>();

        foreach (var type in file.file.Metadata.TypeTreeTypes)
        {
            // Scripted classes are described per script, and which script a loose .dat belongs to
            // is not recoverable from the bytes. Adding one is a different job than this.
            if (type.ScriptTypeIndex != ushort.MaxValue) continue;
            if (type.TypeId == (int)AssetClassID.MonoBehaviour) continue;

            var cls = (AssetClassID)type.TypeId;
            if (found.Contains(cls)) continue;
            if (Reads(context, file, cls, bytes)) found.Add(cls);
        }

        return found;
    }

    /// The leading `Shader` of `Shader-font_color_fix` or `TextMesh #8580476232680033639`.
    private static AssetClassID? ClassIn(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var end = name.IndexOfAny(['-', ' ', '#', '_', '.']);
        var head = end < 0 ? name : name[..end];
        return Enum.TryParse<AssetClassID>(head, ignoreCase: false, out var cls) ? cls : null;
    }

    private static bool AssetAddressableIn(AssetsFileInstance file, AssetClassID cls)
        => cls != AssetClassID.MonoBehaviour
           && file.file.Metadata.TypeTreeTypes.Any(
               t => t.TypeId == (int)cls && t.ScriptTypeIndex == ushort.MaxValue);

    private static bool Reads(
        AssetsContext context, AssetsFileInstance file, AssetClassID cls, byte[] bytes)
    {
        try
        {
            var info = AssetFileInfo.Create(file.file, Scratch, (int)cls, null!, preferEditor: false);
            info.SetNewData(bytes);
            var field = context.Deserialize(file, info);
            if (field is null) return false;

            var written = field.WriteToByteArray();
            return written.Length == bytes.Length && written.AsSpan().SequenceEqual(bytes);
        }
        catch (Exception)
        {
            // A wrong type usually fails by running off the end of the buffer, or by asking for
            // more memory than there is. Both are answers, not faults.
            return false;
        }
    }
}
