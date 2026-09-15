using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// Follows references out of one bundle and into the next.
///
/// A serialized file names its neighbours as `archive:/CAB-<hash>/CAB-<hash>`, which says nothing
/// about which bundle holds them. The CAB index answers that, and costs a tenth of a second across
/// all 2300 bundles because only headers are read — so it is built on first use and kept, rather
/// than cached anywhere on disk.
public sealed class BundleGraph(BundleSet bundles)
{
    private readonly Lazy<CabIndex> _cabs = new(() => CabIndex.Build(bundles));
    private readonly Dictionary<string, AssetsFileInstance> _open = new(StringComparer.OrdinalIgnoreCase);

    public ReferenceWalker.ExternalResolver Resolve => (from, fileId) =>
    {
        // File ids are one-based; zero means the file itself and never reaches here.
        var externals = from.file.Metadata.Externals;
        if (fileId < 1 || fileId > externals.Count) return null;

        var path = externals[fileId - 1].PathName;
        var cab = path[(path.LastIndexOf('/') + 1)..];
        if (_cabs.Value.BundleFor(cab) is not { } bundle) return null;

        try
        {
            if (!_open.TryGetValue(bundle, out var file)) _open[bundle] = file = bundles.Open(bundle);
            return (file, bundle);
        }
        catch (IOException)
        {
            return null;
        }
    };
}
