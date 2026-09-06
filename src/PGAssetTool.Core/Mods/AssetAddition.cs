using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Mods;

/// The rules an object has to satisfy before it can be put into a bundle that did not have it.
///
/// Replacing an asset inherits everything about its surroundings — its type, its stream, whatever
/// points at it — because the asset it replaces already had all of that. An addition arrives with
/// none of it, so each has to be checked instead of assumed. Every answer here is a refusal with a
/// reason rather than a repair: a pack that would need one of these is a pack built against
/// something other than this installation, and quietly making it fit would produce a bundle that
/// loads and then behaves wrongly.
public static class AssetAddition
{
    /// Why this object cannot go into this file, or null when it can.
    public static string? Rejects(
        AssetsContext context, AssetsFileInstance file, AssetClassID cls,
        AssetTypeValueField field, string name)
    {
        if (!HasType(file, cls))
            return $"'{Path.GetFileName(file.name)}' carries no type information for {cls}, so an "
                + $"object of that class cannot be added to it. Only classes the bundle already "
                + "holds can be added.";

        var stream = field["m_StreamData"];
        if (!stream.IsDummy && stream["path"] is { IsDummy: false } path && path.AsString?.Length > 0)
            return $"this {cls} keeps its payload in '{path.AsString}' rather than on the object. An "
                + "added asset has to carry everything it needs.";

        var externals = file.file.Metadata.Externals.Count;
        foreach (var (at, pointer) in PointerPath.All(field))
        {
            var fileId = pointer["m_FileID"].AsInt;
            if (fileId < 0 || fileId > externals)
                return $"its {(at.Length == 0 ? "pointer" : at)} refers to file {fileId}, which "
                    + $"'{Path.GetFileName(file.name)}' does not list ({externals} external file(s)).";
        }

        if (name.Length > 0 && Existing(context, file, cls, name) is { } clash)
            return $"'{Path.GetFileName(file.name)}' already has a {cls} called '{name}' "
                + $"(path id {clash}). Give the added one a different name.";

        return null;
    }

    public static bool HasType(AssetsFileInstance file, AssetClassID cls)
        => file.file.Metadata.TypeTreeTypes.Any(t => t.TypeId == (int)cls);

    /// The path id of an asset of this class already using the name, if there is one.
    private static long? Existing(
        AssetsContext context, AssetsFileInstance file, AssetClassID cls, string name)
    {
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)cls) continue;
            if (AssetNaming.NameOf(context.Deserialize(file, info), cls) == name) return info.PathId;
        }
        return null;
    }
}
