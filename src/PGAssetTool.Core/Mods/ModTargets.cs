using PGAssetTool.Core.Addressables;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Mods;

/// One bundle a mod writes to, found.
///
/// <paramref name="Hash"/> is the version of it: the hash the manifest records on the old path, the
/// content hash the catalog records on the new one. Either way it changes when the game replaces
/// the bundle, which is what keeps a backup taken before an update from being mistaken for the
/// original of what replaced it.
public sealed record BundleTarget(
    string Bundle, string Hash, CacheKind Cache, string Path, CatalogBundle? Delivered)
{
    /// Whether the content catalog is the thing that names this bundle, which is the only case with
    /// a number in that catalog to be kept in step with the bytes.
    public bool InTheCatalog => Delivered is not null;
}

/// Where the bundles a mod writes to live, across both of the game's layouts.
///
/// A 26.15 installation has them at once and a pack can name a bundle in either, so this answers
/// for both and nothing above it asks which. The old path names a bundle in its manifest and keeps
/// the file under a hash directory; the catalog names it by its file name and keeps it in the cache
/// the game downloads into, or beside the install for the ones that ship.
///
/// The names cannot collide: a bundle on the old path has no extension and every one the catalog
/// names ends `.bundle`.
public sealed class ModTargets
{
    private readonly GameInstallation _game;
    private readonly AddressableBundles? _content;
    private readonly Dictionary<string, string> _versions;

    public ModTargets(GameInstallation game, AddressableBundles? content = null)
    {
        _game = game;
        _content = content;

        _versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in game.ReadManifest()) _versions[entry.Name] = entry.Hash;
        if (content is not null)
            foreach (var bundle in content.Catalog.Bundles) _versions[bundle.FileName] = bundle.Hash;
    }

    /// Every bundle that can be written to, and the version it is at now.
    public IReadOnlyDictionary<string, string> Versions => _versions;

    public ContentCatalog? Catalog => _content?.Catalog;

    /// Where the copy the game would load is, or null when there is none.
    ///
    /// The hash is the version being asked for and is checked against the one the game has: a mod
    /// recorded against a bundle an update has replaced is not written into its replacement.
    public BundleTarget? Resolve(string bundle, string hash)
    {
        if (_content?.Catalog.ByFileName(bundle) is { } delivered)
        {
            if (!string.Equals(delivered.Hash, hash, StringComparison.OrdinalIgnoreCase)) return null;
            return _content.Resolve(delivered) is { } where
                ? new BundleTarget(bundle, hash, CacheKind.Content, where.Path, delivered)
                : null;
        }

        return _game.Resolve(bundle, hash) is { } resolved
            ? new BundleTarget(bundle, hash, resolved.Cache, resolved.Path, null)
            : null;
    }

    /// Where a backup taken from a given cache belongs, which is not necessarily the copy the game
    /// now loads: once the downloaded cache claims a bundle it wins, and the shipped copy beneath
    /// it still has to be put back.
    public string? PathIn(CacheKind cache, string bundle, string hash) => cache switch
    {
        CacheKind.Content => _content?.Catalog.ByFileName(bundle) is { } delivered
            && string.Equals(delivered.Hash, hash, StringComparison.OrdinalIgnoreCase)
                ? _content.Resolve(delivered)?.Path
                : null,
        CacheKind.Downloaded => _game.Downloaded?.ClaimedPath(bundle),
        _ => System.IO.Path.Combine(_game.BundlesDirectory, bundle, hash, bundle),
    };

    /// Whether the file at this target is the game's own, as it delivered it.
    ///
    /// The old path has the manifest's hash, which is an MD5 of the whole file. The catalog has the
    /// CRC the game checks a downloaded bundle against — which costs a decompress, and in exchange
    /// needs no manifest and no backup to tell a delivered bundle from an edited one.
    ///
    /// It holds for a bundle that ships with the install as well, and that is worth saying because
    /// it did not look that way: on the installation this was written against, two shipped bundles
    /// disagreed with the catalog by thousands of bytes, which reads like the game describing a
    /// different build. Verifying the game's files through its store replaced both with files that
    /// agree exactly. They were leftovers from an earlier session's experiments, and this answering
    /// "modified by something else" for them was right.
    public bool ReadsAsDelivered(BundleTarget target)
    {
        if (target.Delivered is not { } delivered)
            return BundleIntegrity.IsPristine(target.Path, target.Hash);

        // No check recorded means this tool took the check off, which it only does for a bundle it
        // wrote. See `ModApplier.SettleCatalog`.
        if (delivered.Crc == BundleNumbers.Unchecked) return false;

        try { return BundleCrc.OfBundle(target.Path) == delivered.Crc; }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    /// What the catalog is to say about a bundle this tool wrote: its length, and no check.
    ///
    /// The CRC is deliberately not computed. Hashing it would mean decompressing the whole bundle
    /// for a number that is thrown away, and a number the catalog is not to carry — see
    /// `ModApplier.SettleCatalog`.
    public static BundleNumbers? WrittenNumbersFor(string path)
    {
        try { return new BundleNumbers(BundleNumbers.Unchecked, new FileInfo(path).Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Whether the game will throw this bundle away rather than load it.
    ///
    /// The catalog records a check that the bytes on disk cannot pass. A bundle this tool wrote
    /// carries no check at all, so this is only ever true when something has put the catalog back
    /// without putting the bundle back — which is what a game update does. The player then loses
    /// everything in that bundle, not only the mod in it, and for the one every weapon is resident
    /// in that is every weapon.
    ///
    /// The length stands in for the CRC. Checking the CRC means decompressing the whole bundle,
    /// seventy-five megabytes for that one, and a repacked bundle does not come out the length the
    /// game recorded.
    public bool WillBeRefused(string bundle)
    {
        if (_content?.Catalog.ByFileName(bundle) is not { } delivered) return false;
        if (delivered.Crc == BundleNumbers.Unchecked) return false;
        if (_content.Resolve(delivered) is not { } where) return false;

        try { return new FileInfo(where.Path).Length != delivered.Size; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// What the catalog would have to say about the bytes now at this target, or null for a file it
    /// cannot read. Only a bundle the catalog names has an answer.
    public static BundleNumbers? NumbersFor(string path)
    {
        try
        {
            return BundleCrc.OfBundle(path) is { } crc
                ? new BundleNumbers(crc, new FileInfo(path).Length)
                : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
