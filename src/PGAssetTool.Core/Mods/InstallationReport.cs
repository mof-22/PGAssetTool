using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Mods;

public enum BundleState
{
    /// Byte for byte what the game recorded.
    Vanilla,

    /// Changed, and an installed mod says it changed it.
    ChangedByThisTool,

    /// Changed, and nothing here claims responsibility. Backing one up as the original would put
    /// somebody else's edit on record as vanilla, with no way back.
    ChangedBySomethingElse,

    /// In the manifest but not on disk. The downloaded cache does this: it claims a bundle whose
    /// file is gone, and the game logs a read failure and falls back to the shipped copy.
    Missing,
}

public sealed record BundleReport(string Bundle, CacheKind? Cache, BundleState State, IReadOnlyList<string> Mods)
{
    public string Where => Cache is null ? "not present" : Cache.ToString()!.ToLowerInvariant();

    public string Explanation => State switch
    {
        BundleState.Vanilla => "as the game shipped it",
        BundleState.ChangedByThisTool => $"changed by {string.Join(", ", Mods)}",
        BundleState.ChangedBySomethingElse => "changed by something outside this tool",
        _ => "claimed by the downloaded cache but not on disk",
    };
}

/// What state the game's bundles are in, and which mod is responsible for each change.
///
/// The same answer the verify command gives, in a form a window can show: knowing before an install
/// that a bundle was already edited elsewhere is what lets someone repair it through Steam first,
/// rather than finding out afterwards when the backup has already recorded that edit as the
/// original.
public static class InstallationReport
{
    /// <param name="everything">
    /// Hash every bundle in the manifest rather than only the ones a mod claims. That is the only
    /// way to find one changed by something else, and it reads 4.8 GB — eleven seconds — so it is
    /// something to ask for, not something to do on the way into a window.
    ///
    /// It covers the old path alone. Sweeping the content the same way means decompressing all
    /// 17,988 bundles the catalog names, which is minutes rather than seconds; `fetch
    /// --check-cached` is that sweep, with a progress count and something to stop it with. The
    /// bundles a mod claims are read through both layouts either way, which is what this is for.
    /// </param>
    public static IReadOnlyList<BundleReport> Read(
        GameInstallation game, ModStore store, bool everything = false,
        Addressables.AddressableBundles? content = null)
    {
        var byBundle = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in store.Read())
            foreach (var bundle in mod.TouchedBundles.Keys)
                (byBundle.TryGetValue(bundle, out var mods) ? mods : byBundle[bundle] = []).Add(mod.Name);

        var targets = new ModTargets(game, content ?? Addressables.AddressableBundles.Find(game.DataDirectory));
        var reports = new List<BundleReport>();

        // Every bundle a mod names, whichever layout holds it, and — when asked for everything —
        // the old path's whole manifest with it. A bundle the catalog names goes through the same
        // `ModTargets` the applier writes through, so the two cannot disagree about where it is.
        IEnumerable<string> looking = byBundle.Keys;
        if (everything)
            looking = looking.Concat(game.ReadManifest().Select(e => e.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var bundle in looking)
        {
            if (targets.Versions.GetValueOrDefault(bundle) is not { } version) continue;

            if (targets.Resolve(bundle, version) is not { } target)
            {
                // Only worth reporting when something claims it; a bundle simply not downloaded yet
                // is the normal state for most of them.
                if (byBundle.TryGetValue(bundle, out var claiming))
                    reports.Add(new BundleReport(bundle, null, BundleState.Missing, claiming));
                continue;
            }

            if (targets.ReadsAsDelivered(target)) continue;

            var mods = byBundle.GetValueOrDefault(bundle, []);
            reports.Add(new BundleReport(bundle, target.Cache,
                mods.Count > 0 ? BundleState.ChangedByThisTool : BundleState.ChangedBySomethingElse,
                mods));
        }

        return reports.OrderBy(r => r.State).ThenBy(r => r.Bundle, StringComparer.Ordinal).ToList();
    }
}
