using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Game;

public enum ConsolidationVerdict
{
    /// Byte-identical to the shipped copy. Deleting it changes nothing the game loads.
    Redundant,

    /// Same version as the shipped copy, but its bytes differ and the shipped copy is the one that
    /// matches its hash. The downloaded file is damaged or edited; the shipped copy is authoritative.
    DownloadedIsDamaged,

    /// Same version, but the shipped copy is the altered one — a mod lives there. Removing the
    /// downloaded copy is what makes that mod take effect.
    ShippedCarriesAMod,

    /// A different version from the one shipped. Deleting it rolls the game back to the older
    /// content, so this is never done automatically.
    NewerVersion,

    /// Nothing shipped under this name at this version. The content exists only here, and deleting
    /// it would lose it.
    OnlyHere,

    /// Claimed by the ledger with no file behind it. The game logs a read failure for each of these
    /// on every launch and falls back; dropping the line silences that.
    BrokenClaim,
}

public sealed record ConsolidationItem(
    string Bundle,
    string DownloadedHash,
    string? ManifestHash,
    ConsolidationVerdict Verdict)
{
    /// Removing it cannot change what the game loads, beyond making it load the shipped copy that
    /// was already the intended one.
    public bool SafeToRemove => Verdict
        is ConsolidationVerdict.Redundant
        or ConsolidationVerdict.DownloadedIsDamaged
        or ConsolidationVerdict.ShippedCarriesAMod
        or ConsolidationVerdict.BrokenClaim;
}

/// Works out what would happen if the downloaded cache were emptied so everything loaded from the
/// game's own folder.
///
/// The appeal is obvious — one place to edit instead of two — but it is only safe per bundle. A
/// downloaded copy can be a newer build than the one that shipped, and can be content that never
/// shipped at all; removing either rolls the game back or breaks it. So each bundle is judged
/// separately and only the ones that provably change nothing are offered up.
public static class CacheConsolidation
{
    public static IReadOnlyList<ConsolidationItem> Plan(GameInstallation game)
    {
        if (game.Downloaded is not { } downloaded) return [];

        var manifest = game.ReadManifest().ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase);
        var items = new List<ConsolidationItem>();

        foreach (var (bundle, hash) in downloaded.Claimed)
        {
            var here = downloaded.PathOf(bundle, hash);
            var manifestHash = manifest.GetValueOrDefault(bundle);

            if (!File.Exists(here))
            {
                items.Add(new ConsolidationItem(bundle, hash, manifestHash, ConsolidationVerdict.BrokenClaim));
                continue;
            }

            if (manifestHash is null || !hash.Equals(manifestHash, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ConsolidationItem(bundle, hash, manifestHash, ConsolidationVerdict.NewerVersion));
                continue;
            }

            var shipped = Path.Combine(game.BundlesDirectory, bundle, manifestHash, bundle);
            if (!File.Exists(shipped))
            {
                items.Add(new ConsolidationItem(bundle, hash, manifestHash, ConsolidationVerdict.OnlyHere));
                continue;
            }

            var shippedMd5 = BundleIntegrity.Md5(shipped);
            var hereMd5 = BundleIntegrity.Md5(here);

            items.Add(new ConsolidationItem(bundle, hash, manifestHash,
                hereMd5 == shippedMd5 ? ConsolidationVerdict.Redundant
                : shippedMd5.Equals(manifestHash, StringComparison.OrdinalIgnoreCase)
                    ? ConsolidationVerdict.DownloadedIsDamaged
                    : ConsolidationVerdict.ShippedCarriesAMod));
        }

        return items.OrderBy(i => i.Verdict).ThenBy(i => i.Bundle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Removes the downloaded copies that cannot change what loads, and rewrites the ledger without
    /// them. Anything else is left alone and reported back.
    public static IReadOnlyList<ConsolidationItem> Apply(
        GameInstallation game, IReadOnlyList<ConsolidationItem> plan)
    {
        if (game.Downloaded is not { } downloaded) return [];

        var removed = new List<ConsolidationItem>();
        foreach (var item in plan.Where(i => i.SafeToRemove))
        {
            var directory = Path.GetDirectoryName(downloaded.PathOf(item.Bundle, item.DownloadedHash))!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

            var bundleDirectory = Path.GetDirectoryName(directory)!;
            if (Directory.Exists(bundleDirectory) && Directory.GetFileSystemEntries(bundleDirectory).Length == 0)
                Directory.Delete(bundleDirectory);

            removed.Add(item);
        }

        var keep = downloaded.Claimed
            .Where(kv => !removed.Any(r => r.Bundle.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"{kv.Key}/{kv.Value}")
            .Order(StringComparer.Ordinal);

        Directory.CreateDirectory(Path.GetDirectoryName(downloaded.NamesPath)!);
        File.WriteAllLines(downloaded.NamesPath, keep);
        return removed;
    }
}
