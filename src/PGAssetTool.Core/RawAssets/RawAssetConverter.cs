using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export;

namespace PGAssetTool.Core.RawAssets;

public sealed record ConversionResult(
    RawAssetFile Source,
    string Bundle,
    AssetClassID Class,
    IReadOnlyList<ExportedAsset> Written);

/// Turns raw exported assets into the editable formats the rest of the tool works in.
///
/// A .dat carries no type of its own, so the class comes from the asset it was taken from. Pointing
/// that asset at the modified bytes lets the containing file's own type tree parse them, which is
/// also why this works for any class without special cases.
public sealed class RawAssetConverter(BundleSet bundles, CabIndex cabs)
{
    private readonly AssetExporter _exporter = new(bundles);

    /// Converts a set together, so names shared by more than one asset can be qualified with the
    /// path id instead of silently overwriting each other.
    public IEnumerable<ConversionResult> ConvertAll(
        IEnumerable<RawAssetFile> sources, string outputDirectory, Action<RawAssetFile, Exception> onError)
    {
        var ordered = sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var repeated = ordered.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in ordered)
        {
            ConversionResult result;
            try
            {
                result = Convert(source, outputDirectory,
                    repeated.Contains(source.Name) ? $"{source.Name}-{source.PathId}" : null);
            }
            catch (Exception ex)
            {
                onError(source, ex);
                continue;
            }
            yield return result;
        }
    }

    public ConversionResult Convert(RawAssetFile source, string outputDirectory, string? fileName = null)
    {
        var bundle = cabs.BundleFor(source.Cab)
            ?? throw new KeyNotFoundException(
                $"'{source.Cab}' is not in this installation, so the type of '{source.Name}' cannot be recovered.");

        var file = bundles.Open(bundle);
        var info = file.file.GetAssetInfo(source.PathId)
            ?? throw new KeyNotFoundException(
                $"'{bundle}' has no asset {source.PathId}. The game may have been updated since this was exported.");

        var cls = (AssetClassID)info.TypeId;
        info.SetNewData(File.ReadAllBytes(source.Path));

        try
        {
            var written = _exporter.Export(bundle, file, info, outputDirectory, fileName);
            return new ConversionResult(source, bundle, cls, written);
        }
        finally
        {
            // The replacement is only there to read the file's bytes through the right type; leaving
            // it staged would leak into anything else reading this bundle in the same session.
            info.SetNewData(Array.Empty<byte>());
            info.Replacer = null;
        }
    }
}
