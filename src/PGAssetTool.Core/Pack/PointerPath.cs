using AssetsTools.NET;

namespace PGAssetTool.Core.Pack;

/// Locates the pointer fields inside a parsed asset, and names them so a manifest can refer to one.
///
/// An added asset is reached by whatever already points at it, and that pointer is a path id — the
/// one number a pack cannot promise. So the pointer is recorded as a place rather than a value:
/// "the m_PathID under m_Shader", filled in at apply time once the asset actually has an id. The
/// path is written literally, array nodes and all, because it is produced and consumed here and
/// being unambiguous is worth more than being short.
public static class PointerPath
{
    /// A PPtr and nothing else: a file index and a path id, no other fields.
    public static bool IsPointer(AssetTypeValueField field)
        => field.Children.Count == 2
           && field.Children[0].FieldName == "m_FileID"
           && field.Children[1].FieldName == "m_PathID";

    /// Every pointer in the asset, with the path that finds it again.
    public static IEnumerable<(string Path, AssetTypeValueField Field)> All(AssetTypeValueField root)
    {
        foreach (var found in Walk(root, "")) yield return found;
    }

    private static IEnumerable<(string, AssetTypeValueField)> Walk(AssetTypeValueField field, string path)
    {
        if (IsPointer(field))
        {
            yield return (path, field);
            yield break;
        }

        var array = field.TemplateField.IsArray;
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];

            // An array's elements all carry the same field name, so the index is what tells them
            // apart. Everything else is named.
            var step = array ? i.ToString() : child.FieldName;
            var below = path.Length == 0 ? step : path + "/" + step;
            foreach (var found in Walk(child, below)) yield return found;
        }
    }

    /// The field a path names, or null if the asset has no such place. Null is the answer whenever
    /// the shape has moved under us — a game update, or a pack built against something else — and
    /// the caller is expected to refuse rather than guess.
    public static AssetTypeValueField? Resolve(AssetTypeValueField root, string path)
    {
        var field = root;
        foreach (var step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.TemplateField.IsArray && int.TryParse(step, out var index))
            {
                if (index < 0 || index >= field.Children.Count) return null;
                field = field.Children[index];
                continue;
            }

            var child = field[step];
            if (child.IsDummy) return null;
            field = child;
        }
        return IsPointer(field) ? field : null;
    }
}
