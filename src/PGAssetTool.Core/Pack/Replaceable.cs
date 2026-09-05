using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Pack;

/// What this tool can put back into the game, and how.
///
/// One list, because the answer is needed in three places that must not drift apart: building a
/// workspace manifest, dispatching an operation at apply time, and deciding what a tree is worth
/// showing. A type gains an entry only once its import path works end to end — listing one earlier
/// would produce packs that fail when applied.
public static class Replaceable
{
    private static readonly (AssetClassID Class, string Operation, string[] Formats)[] Kinds =
    [
        (AssetClassID.Texture2D, PackOperations.ReplaceTexture, ["png"]),
        (AssetClassID.Mesh, PackOperations.ReplaceMesh, ["glb"]),
        (AssetClassID.AudioClip, PackOperations.ReplaceAudio, ["wav", "mp3", "ogg"]),
    ];

    /// The classes something can be written back to.
    public static IReadOnlySet<AssetClassID> Classes { get; } = Kinds.Select(k => k.Class).ToHashSet();

    /// The operation to run for a file, by its extension without the dot.
    public static string? OperationForFormat(string format)
        => Kinds.FirstOrDefault(k => k.Formats.Contains(format, StringComparer.OrdinalIgnoreCase)).Operation;

    public static bool Supports(AssetClassID cls) => Classes.Contains(cls);
}
