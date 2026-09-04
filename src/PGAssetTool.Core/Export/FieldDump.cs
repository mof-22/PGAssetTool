using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AssetsTools.NET;

namespace PGAssetTool.Core.Export;

/// Renders an asset's fields as JSON. This is the readable form for types with no interchange
/// format, and the reference an author edits against when writing a field patch.
public static class FieldDump
{
    // Bounds and curve limits are routinely stored as infinity, which JSON has no literal for.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string ToJson(AssetTypeValueField field) => Convert(field).ToJsonString(Options);

    public static string ToJson(IEnumerable<AssetTypeValueField> fields)
        => new JsonArray(fields.Select(Convert).ToArray()).ToJsonString(Options);

    public static JsonNode Convert(AssetTypeValueField field)
    {
        if (field.TemplateField.IsArray)
            return new JsonArray(field.Children.Select(Convert).ToArray());

        if (field.Children.Count > 0)
        {
            // An array is wrapped in a node carrying only its "Array" child; unwrap it so the JSON
            // reads as a list rather than an object holding a list.
            if (field.Children.Count == 1 && field.Children[0].TemplateField.IsArray)
                return Convert(field.Children[0]);

            var node = new JsonObject();
            foreach (var child in field.Children)
                node[child.FieldName] = Convert(child);
            return node;
        }

        return Scalar(field);
    }

    private static JsonNode Scalar(AssetTypeValueField field) => field.Value?.ValueType switch
    {
        AssetValueType.Bool => JsonValue.Create(field.AsBool),
        AssetValueType.Int8 or AssetValueType.Int16 or AssetValueType.Int32 => JsonValue.Create(field.AsInt),
        AssetValueType.UInt8 or AssetValueType.UInt16 or AssetValueType.UInt32 => JsonValue.Create(field.AsUInt),
        AssetValueType.Int64 => JsonValue.Create(field.AsLong),
        AssetValueType.UInt64 => JsonValue.Create(field.AsULong),
        AssetValueType.Float => JsonValue.Create(field.AsFloat),
        AssetValueType.Double => JsonValue.Create(field.AsDouble),
        AssetValueType.String => JsonValue.Create(field.AsString),
        AssetValueType.ByteArray => JsonValue.Create($"<{field.AsByteArray.Length} bytes>"),
        _ => JsonValue.Create(field.AsString),
    };
}
