using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

public readonly record struct Pointer(int FileId, long PathId)
{
    public bool IsLocal => FileId == 0;
}

public sealed record AssetNode(long PathId, AssetClassID Class, string Name)
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

    /// Everything reachable from a starting object without leaving its serialized file. Only the
    /// objects actually reached are deserialized, which is what keeps a single-item lookup fast
    /// enough that no precomputed edge table is needed.
    public static List<AssetNode> Closure(AssetsContext context, AssetsFileInstance file, long rootPathId)
    {
        var seen = new HashSet<long>();
        var nodes = new List<AssetNode>();
        var queue = new Queue<long>();
        queue.Enqueue(rootPathId);

        while (queue.Count > 0)
        {
            var pathId = queue.Dequeue();
            if (!seen.Add(pathId)) continue;

            var info = file.file.GetAssetInfo(pathId);
            if (info is null) continue;

            var field = context.Deserialize(file, info);
            var name = field?["m_Name"];
            nodes.Add(new AssetNode(pathId, (AssetClassID)info.TypeId,
                name is null || name.IsDummy ? "" : name.AsString));

            if (field is null) continue;
            foreach (var pointer in Pointers(field))
                if (pointer.IsLocal) queue.Enqueue(pointer.PathId);
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
