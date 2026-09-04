using System.Collections.Concurrent;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.RawAssets;

/// Maps a serialized file's CAB name to the bundle holding it.
///
/// Built by opening every bundle's header, which costs a fraction of a second across all of them
/// because no asset data is read, so there is nothing to cache or keep in step with the game.
public sealed class CabIndex
{
    private readonly Dictionary<string, string> _bundleByCab;

    private CabIndex(Dictionary<string, string> bundleByCab) => _bundleByCab = bundleByCab;

    public int Count => _bundleByCab.Count;

    public string? BundleFor(string cab) => _bundleByCab.GetValueOrDefault(cab);

    public static CabIndex Build(BundleSet bundles)
    {
        var found = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(bundles.BundleNames, bundle =>
        {
            var context = new AssetsContext();
            try
            {
                var file = context.OpenBundle(bundles.PathOf(bundle));
                foreach (var (_, cab) in AssetsContext.SerializedEntries(file)) found[cab] = bundle;
            }
            catch (IOException) { }
            finally { context.Dispose(); }
        });

        return new CabIndex(new Dictionary<string, string>(found, StringComparer.OrdinalIgnoreCase));
    }
}
