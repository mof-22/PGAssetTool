using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Mods;

/// An enabled mod the game is not loading, and which of its bundles are why.
///
/// `Replaced` is a bundle the update swapped out: the game loads the new one and the mod is simply
/// not in it. `Rejected` is a bundle that still holds the mod and that the game throws away whole,
/// because the catalog records a check those bytes cannot pass — the player loses everything in it,
/// not only the mod.
public sealed record StaleMod(
    InstalledMod Mod, IReadOnlyList<string> Replaced, IReadOnlyList<string> Rejected)
{
    public IReadOnlyList<string> Bundles => [.. Replaced, .. Rejected];
}

/// Whether the game has been updated under the mods installed into it.
///
/// An update replaces the bundles a mod was written into. The game then loads the new, unmodified
/// ones, so every mod on them is simply not in the game any more — and nothing about the tool looks
/// wrong, because the ledger still lists them all as on.
///
/// Noticed from what each mod already records: the hash of every bundle it wrote to, which a
/// reconcile keeps current even for a bundle it left alone. The manifest names the hash the game
/// loads now, so a mod with a bundle whose hash has moved — or which has gone — is one the update
/// took out. Reapplying writes it into the new bundle and records the new hash, which is what
/// makes the notice go away. The version the game reports is only for saying so; hot fixes that
/// keep the version would slip past it, and bundles do not.
///
/// **An update can also leave a bundle exactly where it was and still break it.** It rewrites the
/// catalog, which takes off the mark that turns off the check on a bundle this tool wrote; the mod
/// bytes are still there, the check is back, and the game refuses the whole bundle. The hash has
/// not moved, so nothing above tells that from a healthy install — which is why `ModTargets` is
/// asked as well as the hashes.
public static class GameUpdate
{
    public static IReadOnlyList<StaleMod> Stale(IEnumerable<InstalledMod> mods, IEnumerable<BundleEntry> manifest)
        => Stale(mods, manifest.ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<StaleMod> Stale(IEnumerable<InstalledMod> mods, ModTargets targets)
        => Stale(mods, targets.Versions, targets.WillBeRefused);

    /// <param name="now">
    /// Every bundle that can be written to and the version it is at, across both of the game's
    /// layouts — see ModTargets. An update replaces the catalog as surely as it replaces the
    /// manifest, and a mod written into a bundle neither names any more is just as stale.
    /// </param>
    /// <param name="refused">
    /// Whether the game will throw a bundle away as it stands — see ModTargets.WillBeRefused. Asked
    /// only of a bundle still at the version the mod was written into, because a bundle that has
    /// moved is already answered for.
    /// </param>
    public static IReadOnlyList<StaleMod> Stale(
        IEnumerable<InstalledMod> mods,
        IReadOnlyDictionary<string, string> now,
        Func<string, bool>? refused = null)
    {
        return mods
            .Where(m => m.Enabled)
            .Select(m =>
            {
                List<string> replaced = [], rejected = [];
                foreach (var (bundle, hash) in m.TouchedBundles)
                {
                    if (!now.TryGetValue(bundle, out var at)
                        || !string.Equals(at, hash, StringComparison.OrdinalIgnoreCase))
                        replaced.Add(bundle);
                    else if (refused?.Invoke(bundle) == true)
                        rejected.Add(bundle);
                }

                return new StaleMod(m, Sorted(replaced), Sorted(rejected));
            })
            .Where(s => s.Replaced.Count > 0 || s.Rejected.Count > 0)
            .ToList();
    }

    private static List<string> Sorted(List<string> bundles)
    {
        bundles.Sort(StringComparer.OrdinalIgnoreCase);
        return bundles;
    }

    /// A sentence for the top of the window, or null when there is nothing to say.
    /// <param name="version">What the game reports itself as now, when that could be read.</param>
    public static string? Notice(IReadOnlyList<StaleMod> stale, string? version)
    {
        if (stale.Count == 0) return null;

        var was = stale
            .Select(s => s.Mod.GameVersion)
            .Where(v => v.Length > 0 && v != "unknown")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var from = was.Count == 1 && was[0] != version ? $" from {was[0]}" : "";
        var to = version is { Length: > 0 } ? $" to {version}" : "";
        var which = stale.Count == 1 ? $"'{stale[0].Mod.Name}' was" : $"{stale.Count} of your mods were";

        // The two cost the player different things, and the one that costs more is worth saying
        // first: a bundle the game refuses takes everything in it, not just the mod.
        var refused = stale.Sum(s => s.Rejected.Count);
        var lost = refused == 0 ? "" : refused == 1
            ? " One of the bundles will not load at all until it is put back or written again, and "
                + "nothing in it is in the game meanwhile."
            : $" {refused} of the bundles will not load at all until they are put back or written "
                + "again, and nothing in them is in the game meanwhile.";

        return $"The game has been updated{from}{to} since {which} installed, and the bundles it loads now "
            + $"do not have them in.{lost} Reapplying writes them into the new ones.";
    }
}
