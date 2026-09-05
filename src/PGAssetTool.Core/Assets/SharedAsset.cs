using System.Text.RegularExpressions;

namespace PGAssetTool.Core.Assets;

/// One replaceable object that more than one weapon reaches.
public sealed record SharedAsset(AssetAddress Target, IReadOnlyList<string> Prefabs, IReadOnlyList<int> Weapons)
{
    public override string ToString()
        => $"{Target} is shared by {Weapons.Count} weapons ({string.Join(", ", Prefabs)}); "
           + "replacing it changes all of them.";
}

/// Decides whether replacing something would reach past the weapon it was taken from.
public static partial class SharedAssets
{
    /// A weapon owns several prefabs — the weapon itself, an `_info` variant for the preview, one
    /// per skin — and they legitimately share a texture or a sound. Only the number distinguishes
    /// one weapon from another, so counting prefab names would warn about almost everything.
    [GeneratedRegex(@"^(?:Weapon|Ray)(\d+)")]
    private static partial Regex PrefabNumber { get; }

    public static SharedAsset? Check(BundleUsage usage, AssetAddress target, long pathId)
        => Check(target, usage.PrefabsUsing(pathId));

    public static SharedAsset? Check(AssetAddress target, IReadOnlyList<string> prefabs)
    {
        var weapons = prefabs
            .Select(p => PrefabNumber.Match(p))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].ValueSpan))
            .Distinct()
            .Order()
            .ToList();

        // Anything not named for a weapon is counted as its own user, so a shared UI material or a
        // mesh two unrelated props sit on is still reported rather than silently passed over.
        var unnamed = prefabs.Count(p => !PrefabNumber.IsMatch(p));

        return weapons.Count + unnamed > 1 ? new SharedAsset(target, prefabs, weapons) : null;
    }
}
