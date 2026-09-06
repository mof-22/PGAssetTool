using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Pack;

public static class PackOperations
{
    public const string ReplaceTexture = "replaceTexture";
    public const string ReplaceMesh = "replaceMesh";
    public const string ReplaceAudio = "replaceAudio";

    /// Writes an asset's serialized bytes back as they are. The one operation that works for a
    /// class nothing else here understands, because it does not have to understand it either.
    public const string ReplaceRaw = "replaceRaw";

    /// Puts an object into a bundle that had none. See PackOperation.NewId for why it needs one.
    public const string AddAsset = "addAsset";

    /// Whether an operation needs the manifest to declare format version 2.
    public static bool NeedsVersion2(string op) => op is ReplaceRaw or AddAsset;
}

/// Where a pointer lives and what it should be made to point at.
///
/// `Path` names the place inside the asset (see PointerPath); `NewId` is the added asset it refers
/// to, by the pack-local handle rather than by a number.
public sealed record PointerFixup
{
    public required string Path { get; init; }
    public required string NewId { get; init; }
}

public sealed record PackOperation
{
    public required string Op { get; init; }
    public required AssetAddress Target { get; init; }
    public required string Source { get; init; }

    /// Hash of the file as it was exported. An unchanged file is not a modification, so packing
    /// drops it. Stripped from the manifest that goes into the pack.
    public string? BaselineSha256 { get; init; }

    /// What an added asset is called inside this pack, for the length of one apply.
    ///
    /// Not a path id. The id an asset was built with belongs to whoever built it, and nothing can
    /// promise the same number is free in the player's bundle — today, or after the next update
    /// moves things around. `Target.PathId` is still recorded and still tried first, but it is a
    /// preference; this handle is the identity, and applying resolves it to whatever id the asset
    /// actually got. Everything pointing at the asset is written in terms of this.
    public string? NewId { get; init; }

    /// Pointers inside this operation's asset that name an added asset instead of a number.
    public List<PointerFixup> Pointers { get; init; } = [];
}

public sealed record PackManifest
{
    public const int CurrentFormatVersion = 2;
    public const string FileName = "pgmod.json";

    /// Version 1 is what a pack of texture, mesh and audio replacements has always been. Version 2
    /// added raw replacement and asset addition, and a build claims it only when it uses them: a
    /// pack of the old operations stays readable by the builds that predate this, which is the
    /// whole point of writing a version down.
    public static int VersionFor(IEnumerable<PackOperation> operations)
        => operations.Any(o => PackOperations.NeedsVersion2(o.Op)) ? 2 : 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";

    /// Whether the built pack is signed and scrambled. Null follows the setting, so a workspace
    /// made before anybody chose does whatever the tool is set to now.
    public bool? Protect { get; init; }

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
