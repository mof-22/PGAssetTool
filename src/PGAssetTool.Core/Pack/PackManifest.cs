using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Pack;

public static class PackOperations
{
    public const string ReplaceTexture = "replaceTexture";
    public const string ReplaceMesh = "replaceMesh";
    public const string ReplaceAudio = "replaceAudio";
}

public sealed record PackOperation
{
    public required string Op { get; init; }
    public required AssetAddress Target { get; init; }
    public required string Source { get; init; }

    /// Hash of the file as it was exported. An unchanged file is not a modification, so packing
    /// drops it. Stripped from the manifest that goes into the pack.
    public string? BaselineSha256 { get; init; }
}

public sealed record PackManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "pgmod.json";

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";

    /// A picture of the pack, as a path inside it. Empty means it has none.
    ///
    /// Whoever installs a pack sees a list of names, and a name is a poor way to tell one weapon
    /// re-skin from another. Extraction points this at the weapon's own icon, which is already
    /// being written out, so a pack has one without anybody deciding to give it one — and an author
    /// who would rather show their own work can point it somewhere else.
    ///
    /// Packed even when it is not one of the files being replaced, which is the usual case: the
    /// icon is what the pack looks like, not part of what it does.
    public string Icon { get; init; } = "";

    /// Recorded, not enforced. Refusing to apply a pack built against an older version would break
    /// every pack on every update, which is the opposite of what the address design is for.
    public string? BuiltAgainstGameVersion { get; init; }

    public List<PackOperation> Operations { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PackManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<PackManifest>(json, Json)
            ?? throw new InvalidDataException("The manifest is empty.");
        if (manifest.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"This pack needs format version {manifest.FormatVersion}; this build understands {CurrentFormatVersion}.");
        return manifest;
    }
}
