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

    /// An asset's own serialized bytes, which every class can be written back from.
    ///
    /// Deliberately not one of the Kinds above. Those are interchange formats: a class earns a
    /// place there once an author can open the file in something, change it, and get a working
    /// asset back. A .dat is not an interchange format — it is the asset already, and it round
    /// trips exactly for any class precisely because nothing here has to understand the class.
    /// Keeping the two apart is what stops "Material is replaceable" from coming to mean "a
    /// Material can be edited here", which it does not.
    public const string RawFormat = "dat";

    /// The classes something can be written back to.
    public static IReadOnlySet<AssetClassID> Classes { get; } = Kinds.Select(k => k.Class).ToHashSet();

    /// Classes this tool neither shows nor writes.
    ///
    /// A component is the game's own configuration of how a thing *behaves*, and its script says
    /// which configuration it is. This tool exists to change how things look and sound; behaviour
    /// is a different question with different consequences — it reaches other players, where a
    /// texture does not.
    ///
    /// Not writing them is enforced at every door: converting, building, and applying. Applying is
    /// the one that holds, because a pack built by something else never went past the other two.
    ///
    /// Not showing them is a separate decision and a weaker one — anything that opens the bundles
    /// shows the same thing, and this makes no claim to prevent that. What it does is keep the tool
    /// from being where somebody first learns the idea. Nothing else in the tool is hidden, and
    /// nothing else should be: this is the exception, not a habit.
    private static readonly AssetClassID[] Withheld =
        [AssetClassID.MonoBehaviour, AssetClassID.MonoScript];

    /// Whether this tool will write an asset of this class back at all, in any format.
    public static bool CanWriteBack(AssetClassID cls) => !Withheld.Contains(cls);

    /// Whether this tool lists an asset of this class at all — in the tree, in a field dump, or in
    /// anything the command line prints.
    ///
    /// References are still followed *through* these on the way to what they point at, so a texture
    /// a component names is found exactly as before. It is the row itself that is not shown.
    public static bool CanShow(AssetClassID cls) => !Withheld.Contains(cls);

    /// Said the same way wherever it is said, so the answer does not depend on which door it was
    /// asked at.
    public static string WhyRefused(AssetClassID cls, string what)
        => $"'{what}' is a {cls}, which this tool does not write back. It changes how things look "
            + "and sound; a component decides how they behave.";

    /// The operation to run for a file, by its extension without the dot.
    public static string? OperationForFormat(string format)
        => string.Equals(format, RawFormat, StringComparison.OrdinalIgnoreCase)
            ? PackOperations.ReplaceRaw
            : Kinds.FirstOrDefault(k => k.Formats.Contains(format, StringComparer.OrdinalIgnoreCase)).Operation;

    public static bool Supports(AssetClassID cls) => Classes.Contains(cls);
}
