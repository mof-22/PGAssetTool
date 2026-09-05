using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

public readonly record struct Pointer(int FileId, long PathId)
{
    public bool IsLocal => FileId == 0;
}

/// `Bundle` is carried because a weapon does not fit in one: its materials and their textures live
/// in a bundle of their own, reached through the serialized file's external reference table.
public sealed record AssetNode(long PathId, AssetClassID Class, string Name, string Bundle = "")
{
    public override string ToString() => Name.Length > 0 ? $"{Class} '{Name}'" : Class.ToString();
}

public static class ReferenceWalker
{
    /// Every PPtr reachable in an object's fields, at any nesting depth.
    public static IEnumerable<Pointer> Pointers(AssetTypeValueField field, int maxDepth = 12)
        => Walk(field, 0, maxDepth);

    private static IEnumerable<Pointer> Walk(AssetTypeValueField field, int depth, int maxDepth)
    {
        if (depth > maxDepth) yield break;
        foreach (var child in field.Children)
        {
            if (child.TemplateField.Type.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                var fileId = child["m_FileID"];
                var pathId = child["m_PathID"];
                if (!fileId.IsDummy && !pathId.IsDummy && pathId.AsLong != 0)
                    yield return new Pointer(fileId.AsInt, pathId.AsLong);
            }
            else
            {
                foreach (var pointer in Walk(child, depth + 1, maxDepth)) yield return pointer;
            }
        }
    }

    /// Opens the serialized file an external reference points at, or answers null when the tool
    /// cannot place it — `Resources/unity default resources` is not a bundle and never resolves.
    public delegate (AssetsFileInstance File, string Bundle)? ExternalResolver(AssetsFileInstance from, int fileId);

    /// A weapon does not fit in one bundle. The prefab, its meshes and its sounds ship together,
    /// but the materials and the textures they use sit in a shared bundle and are reached through
    /// the file's external reference table. Following only local pointers stops at the renderer and
    /// misses every model texture — which is the thing most mods replace.
    ///
    /// Two hops is where it converges in practice: prefab to material bundle, material to whatever
    /// that bundle borrows. Measured across several weapons, going deeper adds nothing, and the cap
    /// keeps a stray reference from dragging in half the game.
    public const int DefaultBundleHops = 2;

    /// Everything reachable from a starting object. Only the objects actually reached are
    /// deserialized, which is what keeps a single-item lookup fast enough that no precomputed edge
    /// table is needed.
    public static List<AssetNode> Closure(
        AssetsContext context, AssetsFileInstance file, long rootPathId,
        ExternalResolver? external = null, int maxHops = DefaultBundleHops,
        IReadOnlySet<AssetClassID>? skip = null, int maxObjects = 20000)
    {
        var start = (File: file, Bundle: "");
        var seen = new HashSet<(string, long)>();
        var nodes = new List<AssetNode>();
        var queue = new Queue<((AssetsFileInstance File, string Bundle) Where, long PathId, int Hops)>();
        queue.Enqueue((start, rootPathId, 0));

        while (queue.Count > 0 && nodes.Count < maxObjects)
        {
            var (where, pathId, hops) = queue.Dequeue();
            if (!seen.Add((where.Bundle, pathId))) continue;

            var info = where.File.file.GetAssetInfo(pathId);
            if (info is null) continue;

            // Reading the class is free; deserializing is not. Shaders are the reason this exists:
            // one dumps to thirty megabytes of JSON, nothing here can replace one, and following a
            // material into its shader wanders off into engine plumbing shared by the whole game.
            if (skip is not null && skip.Contains((AssetClassID)info.TypeId)) continue;

            var field = context.Deserialize(where.File, info);
            var name = field?["m_Name"];
            nodes.Add(new AssetNode(pathId, (AssetClassID)info.TypeId,
                name is null || name.IsDummy ? "" : name.AsString, where.Bundle));

            if (field is null) continue;
            foreach (var pointer in Pointers(field))
            {
                if (pointer.IsLocal) { queue.Enqueue((where, pointer.PathId, hops)); continue; }
                if (hops >= maxHops || external is null) continue;
                if (external(where.File, pointer.FileId) is { } next)
                    queue.Enqueue((next, pointer.PathId, hops + 1));
            }
        }
        return nodes;
    }

    public static AssetFileInfo? FindByName(
        AssetsContext context, AssetsFileInstance file, AssetClassID cls, string name)
    {
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)cls) continue;
            var field = context.Deserialize(file, info);
            if (field is not null && string.Equals(field["m_Name"].AsString, name, StringComparison.Ordinal))
                return info;
        }
        return null;
    }
}
