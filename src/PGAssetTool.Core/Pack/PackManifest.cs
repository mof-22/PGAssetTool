using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Pack;

public static class PackOperations
{
    public const string ReplaceTexture = "replaceTexture";
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
