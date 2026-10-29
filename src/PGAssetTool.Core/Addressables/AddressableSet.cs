using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Addressables;

/// Address-addressed access to the game's content: what a key loads, and the object behind it.
///
/// The counterpart of `BundleSet` for the Addressables path, and deliberately not the same class.
/// The two layouts share nothing — a bundle is named by a hash rather than by a name, there is no
/// manifest, what is on disk is whatever the player has reached, and a bundle carries the CRC its
/// contents must hash to. The old path keeps its own reader for its 90 bundles.
public sealed class AddressableSet : IDisposable
{
    private readonly AssetsContext _context;
    private readonly bool _ownsContext;
    private readonly Func<CatalogBundle, string?>? _originals;

    /// Every bundle opened through here, by the path it was opened from, so each file is opened
    /// exactly once.
    ///
    /// Asking the manager for the same path twice does not reliably hand back the same instance,
    /// and the second one is a second open file that disposing the manager does not close. Reading
    /// a texture's pixels is two asks — one for the object, one for the stream the pixels live in —
    /// so left to itself this leaves the bundle locked after the reader is disposed, and the next
    /// write to it fails with the file in use.
    private readonly Dictionary<string, BundleFileInstance> _opened =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Dictionary<string, long>> _containers =
        new(StringComparer.OrdinalIgnoreCase);

    private long _unpacked;

    /// <param name="originals">
    /// Asked for the copy of a bundle as it was before anything was written into it, answering null
    /// for one nothing has touched. Given, this reads the game as it was delivered; left out, it
    /// reads the game as it is. Anything that shows or extracts an item wants the first.
    /// </param>
    public AddressableSet(
        AddressableBundles bundles, AssetsContext? context = null,
        Func<CatalogBundle, string?>? originals = null)
    {
        Bundles = bundles;
        _ownsContext = context is null;
        _context = context ?? new AssetsContext(ClassPackage.Locate());
        _originals = originals;
    }

    public AddressableBundles Bundles { get; }
    public ContentCatalog Catalog => Bundles.Catalog;
    public AssetsContext Context => _context;

    /// How many bytes of bundles this reader may hold unpacked in memory; see BundleUnpacker. What
    /// does not fit is read from disk a block at a time. Zero unpacks nothing.
    public long UnpackBudget { get; init; }

    /// How much of the budget is in use.
    public long Unpacked => _unpacked;

    /// The copy to read: the kept original where there is one, otherwise what the game would load.
    public string PathOf(CatalogBundle bundle)
    {
        if (_originals?.Invoke(bundle) is { } original && File.Exists(original)) return original;

        return Bundles.Resolve(bundle)?.Path
               ?? throw new FileNotFoundException(
                   bundle.IsRemote
                       ? $"'{bundle.FileName}' has not been downloaded."
                       : $"'{bundle.FileName}' is not in the installation.");
    }

    /// Whether this bundle is on disk at all. A remote one the player has never reached is not, and
    /// there is no way to fetch it from here or from inside the game.
    public bool Holds(CatalogBundle bundle) => Bundles.Resolve(bundle) is not null;

    /// The first serialized file in a bundle. Every content bundle here holds exactly one.
    public AssetsFileInstance Open(CatalogBundle bundle)
    {
        var file = Bundle(bundle);
        var entry = AssetsContext.SerializedEntries(file).First();
        return _context.OpenBundleEntry(file, entry.Index);
    }

    private BundleFileInstance Bundle(CatalogBundle bundle)
    {
        var path = PathOf(bundle);
        if (_opened.TryGetValue(path, out var already)) return already;

        if (UnpackBudget > _unpacked && BundleUnpacker.Unpack(path, UnpackBudget - _unpacked) is { } unpacked)
        {
            _unpacked += unpacked.Length;
            return _opened[path] = _context.OpenBundle(unpacked, path);
        }

        return _opened[path] = _context.OpenBundle(path);
    }

    /// The object a bundle loads under an asset path, or null if it names none.
    ///
    /// This is the game's own answer: a bundle's `m_Container` maps the asset paths the catalog
    /// hands out to the objects they name, so nothing has to be searched for by name — and a name
    /// is not unique inside a bundle. A weapon-map bundle names 1,262 of them, which is why the
    /// table is read once and kept as a lookup.
    ///
    /// Answers nothing rather than throwing for a bundle that will not open, since a caller asking
    /// where something is has nothing better to do with the news than leave the row unresolved.
    public AssetNode? Locate(CatalogBundle bundle, string assetPath)
    {
        AssetsFileInstance file;
        try { file = Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException or NotSupportedException)
        {
            return null;
        }

        if (!Container(bundle, file).TryGetValue(assetPath, out var pathId)) return null;
        if (file.file.GetAssetInfo(pathId) is not { } info) return null;
        return new AssetNode(
            pathId, (AssetClassID)info.TypeId, _context.NameOf(file, info), bundle.FileName);
    }

    private Dictionary<string, long> Container(CatalogBundle bundle, AssetsFileInstance file)
    {
        if (_containers.TryGetValue(bundle.Hash, out var cached)) return cached;

        var table = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var info = file.file.AssetInfos.FirstOrDefault(i => i.TypeId == (int)AssetClassID.AssetBundle);
        if (info is not null && _context.Deserialize(file, info)?["m_Container"]["Array"] is
            { IsDummy: false } entries)
        {
            foreach (var entry in entries.Children)
                table[entry["first"].AsString] = entry["second"]["asset"]["m_PathID"].AsLong;
        }
        return _containers[bundle.Hash] = table;
    }

    /// What a key loads, as the objects behind it.
    ///
    /// The bundle holding a location's asset is among the bundles that location depends on, and in
    /// practice it is the first of them; they are tried in order and the first that names the asset
    /// answers, which is right whether or not that holds. A location whose bundles are not on disk
    /// answers nothing, and `Missing` says which they were.
    public IReadOnlyList<AssetNode> Find(string key)
    {
        var found = new List<AssetNode>();
        foreach (var location in Catalog.LocationsFor(key))
            foreach (var bundle in location.Bundles)
            {
                if (!Holds(bundle)) continue;
                if (Locate(bundle, location.InternalId) is not { } node) continue;
                found.Add(node);
                break;
            }
        return found;
    }

    /// The bundles a key needs that are not on disk. An item with any of these is one the player
    /// has never reached, and nothing in the tool or the game can fetch them.
    public IReadOnlyList<string> Missing(string key)
        => Catalog.BundlesFor(key).Where(b => !Holds(b)).Select(b => b.FileName).ToList();

    /// Texture and audio payloads are not in the serialized object; they sit in a sibling .resS or
    /// .resource entry the object points into. This reads that slice.
    public byte[] ReadResource(CatalogBundle bundle, string sourcePath, long offset, long size)
    {
        var wanted = Path.GetFileName(sourcePath);
        var file = Bundle(bundle);
        foreach (var entry in file.file.BlockAndDirInfo.DirectoryInfos)
        {
            if (!string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            // Asked of the entry rather than trusted: where an object says its bytes are is data
            // like any other, and a size that ran off the end would be an enormous allocation on
            // the way to an unreadable answer.
            if (offset < 0 || size < 0 || offset + size > entry.DecompressedSize)
                throw new InvalidDataException(
                    $"'{bundle.FileName}' says an object's bytes are {size} long at {offset} in "
                    + $"'{wanted}', which holds {entry.DecompressedSize}.");

            var reader = file.file.DataReader;
            reader.Position = entry.Offset + offset;
            return reader.ReadBytes((int)size);
        }
        throw new FileNotFoundException($"'{bundle.FileName}' has no stream entry '{wanted}'.");
    }

    public void Dispose()
    {
        _opened.Clear();
        _containers.Clear();
        if (_ownsContext) _context.Dispose();
    }
}
