using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.RawAssets;

public sealed record ConversionResult(
    RawAssetFile Source,
    string Bundle,
    AssetClassID Class,
    IReadOnlyList<ExportedAsset> Written)
{
    /// Set when the game has no asset at this address, so the operation adds one rather than
    /// replacing it. Carries the handle the rest of the pack refers to the new asset by.
    public string? NewId { get; init; }

    /// The asset's own bytes inside the workspace — what a raw replacement or an addition is
    /// written from, as opposed to the readable dump beside it.
    public string? RawPath { get; init; }

    /// Said when the class had to be guessed and the guess was not the only one that fits.
    public IReadOnlyList<AssetClassID> AlsoFits { get; init; } = [];

    /// The asset as parsed, kept so the pointers inside it can be looked at without reading the
    /// bundle a second time.
    public AssetTypeValueField? Field { get; init; }

    public bool IsAddition => NewId is not null;
}

/// Turns raw exported assets into the editable formats the rest of the tool works in.
///
/// A .dat carries no type of its own, so the class comes from the asset it was taken from. Pointing
/// that asset at the modified bytes lets the containing file's own type tree parse them, which is
/// also why this works for any class without special cases.
///
/// When the game has no asset at that path id the mod is adding one rather than changing one. That
/// used to be reported as "the game may have been updated"; it is now the signal that an addition
/// is what this is, and the class comes from the bytes instead (see ClassInference).
public sealed class RawAssetConverter(BundleSet bundles, CabIndex cabs)
{
    private readonly AssetExporter _exporter = new(bundles);

    /// Converts a set and writes a pack manifest over the result, so an existing mod becomes a
    /// packable workspace in one step. The converted files are the modification already, so they
    /// are recorded without a baseline: there is no edit still to come.
    public PackManifest ConvertToWorkspace(
        IEnumerable<RawAssetFile> sources, string outputDirectory, string id, string author,
        string? gameVersion, Action<RawAssetFile, Exception> onError, out List<ConversionResult> results)
    {
        results = ConvertAll(sources, outputDirectory, onError).ToList();
        return Workspace.Create(
            outputDirectory, id, name: id, author: author, gameVersion: gameVersion,
            operations: Operations(outputDirectory, results), icon: "");
    }

    /// The manifest's operations, additions first.
    ///
    /// Order matters at apply time and is cheaper to fix here than to sort out there: an addition
    /// has to exist before the pointers naming it can be filled in. The applier does not rely on
    /// this — it makes its own two passes — but a manifest a person reads should say what happens
    /// in the order it happens.
    private static List<PackOperation> Operations(string directory, List<ConversionResult> results)
    {
        // Which added asset each path id used to be, so a pointer still holding the author's number
        // can be recognised as naming it.
        var added = results
            .Where(r => r.IsAddition)
            .ToDictionary(r => r.Source.PathId, r => r.NewId!);

        var operations = new List<PackOperation>();

        foreach (var result in results.Where(r => r.IsAddition))
            operations.Add(new PackOperation
            {
                Op = PackOperations.AddAsset,
                Target = new AssetAddress(
                    result.Bundle, result.Class.ToString(), NameOf(result), PathId: result.Source.PathId),
                Source = Relative(directory, result.RawPath!),
                NewId = result.NewId,
                Pointers = Fixups(result, added),
            });

        foreach (var result in results.Where(r => !r.IsAddition))
        {
            // A class with an interchange format is edited in that format, and the raw bytes are
            // only the way in for the classes that have none. Offering both for one asset would put
            // two operations on the same address, and whichever ran second would undo the first.
            var wanted = Replaceable.Supports(result.Class)
                ? result.Written.Where(w => w.Format != Replaceable.RawFormat)
                : result.Written.Where(w => w.Path == result.RawPath);

            foreach (var asset in wanted)
            {
                if (Replaceable.OperationForFormat(asset.Format) is not { } op) continue;
                if (asset.Address.Container.Length == 0) continue;

                operations.Add(new PackOperation
                {
                    Op = op,
                    Target = asset.Address,
                    Source = Relative(directory, asset.Path),
                    Pointers = Fixups(result, added),
                });
            }
        }

        return operations;
    }

    /// The pointers in this asset that refer to something the pack adds.
    ///
    /// Found rather than declared. The author's file already points at the new asset by the path id
    /// their editor gave it — that is what made the mod work for them — so every pointer holding
    /// one of those numbers is a pointer that has to be rewritten once the asset is given an id
    /// here. Recording the place instead of the number is what lets that id change.
    private static List<PointerFixup> Fixups(ConversionResult result, Dictionary<long, string> added)
    {
        if (added.Count == 0 || result.Field is null) return [];

        var fixups = new List<PointerFixup>();
        foreach (var (at, pointer) in PointerPath.All(result.Field))
        {
            if (pointer["m_FileID"].AsInt != 0) continue;
            if (!added.TryGetValue(pointer["m_PathID"].AsLong, out var newId)) continue;

            // A pointer at the top of the asset has no path, and nothing can name it. No asset here
            // is itself a PPtr, so this cannot happen; if it ever does, saying nothing is better
            // than recording a fixup that resolves to the whole object.
            if (at.Length == 0) continue;
            fixups.Add(new PointerFixup { Path = at, NewId = newId });
        }
        return fixups;
    }

    private static string NameOf(ConversionResult result)
        => result.Written.Select(w => w.Name).FirstOrDefault() ?? result.Source.Name;

    private static string Relative(string directory, string path)
        => Path.GetRelativePath(directory, path).Replace('\\', '/');

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
        var bytes = File.ReadAllBytes(source.Path);

        return file.file.GetAssetInfo(source.PathId) is { } info
            ? Replacement(source, outputDirectory, fileName, bundle, file, info, bytes)
            : Addition(source, outputDirectory, fileName, bundle, file, bytes);
    }

    private ConversionResult Replacement(
        RawAssetFile source, string outputDirectory, string? fileName,
        string bundle, AssetsFileInstance file, AssetFileInfo info, byte[] bytes)
    {
        var cls = (AssetClassID)info.TypeId;
        if (!Replaceable.CanWriteBack(cls))
            throw new NotSupportedException(Replaceable.WhyRefused(cls, source.Name));

        info.SetNewData(bytes);

        try
        {
            var written = _exporter.Export(bundle, file, info, outputDirectory, fileName, bytes);
            var raw = Replaceable.Supports(cls)
                ? null
                : RawBeside(written, outputDirectory, fileName, source, cls, bytes);

            return new ConversionResult(source, bundle, cls, Combine(written, raw))
            {
                RawPath = raw,
                Field = bundles.Context.Deserialize(file, info),
            };
        }
        finally
        {
            // The replacement is only there to read the file's bytes through the right type; leaving
            // it staged would leak into anything else reading this bundle in the same session.
            info.SetNewData(Array.Empty<byte>());
            info.Replacer = null;
        }
    }

    private ConversionResult Addition(
        RawAssetFile source, string outputDirectory, string? fileName,
        string bundle, AssetsFileInstance file, byte[] bytes)
    {
        var inferred = ClassInference.Infer(bundles.Context, file, bytes, source.Name);
        if (inferred.Candidates.Count == 0)
            throw new InvalidDataException(
                $"'{bundle}' has no asset {source.PathId}, so '{source.Name}' is adding one — but its "
                + "bytes do not read back as any class the bundle describes. It may have been built "
                + "against a different version of the game.");

        var cls = inferred.Candidates[0];
        if (!Replaceable.CanWriteBack(cls))
            throw new NotSupportedException(Replaceable.WhyRefused(cls, source.Name));

        var info = AssetFileInfo.Create(file.file, source.PathId, (int)cls, null!, preferEditor: false);
        info.SetNewData(bytes);
        var field = bundles.Context.Deserialize(file, info)
            ?? throw new InvalidDataException($"'{source.Name}' could not be read as a {cls}.");

        var name = AssetNaming.NameOf(field, cls);
        if (AssetAddition.Rejects(bundles.Context, file, cls, field, name) is { } refusal)
            throw new InvalidDataException($"'{source.Name}' cannot be added to '{bundle}': {refusal}");

        // Written out the same way a replacement is, so an author gets the same readable dump, but
        // from an info that is never put into the file — reading it must not change the bundle the
        // rest of the session is looking at.
        var written = _exporter.Export(bundle, file, info, outputDirectory, fileName, bytes);
        var raw = RawBeside(written, outputDirectory, fileName, source, cls, bytes);

        return new ConversionResult(source, bundle, cls, Combine(written, raw))
        {
            NewId = Handle(name, source),
            RawPath = raw,
            Field = field,
            AlsoFits = inferred.Candidates.Skip(1).ToList(),
        };
    }

    /// What the pack calls the added asset. Its own name where it has one, since that is what an
    /// author reading the manifest will recognise, and the source file's name where it does not.
    private static string Handle(string name, RawAssetFile source)
        => name.Length > 0 ? name : source.Name;

    /// The asset's bytes inside the workspace, written unless the export already put them there.
    private static string RawBeside(
        IReadOnlyList<ExportedAsset> written, string outputDirectory, string? fileName,
        RawAssetFile source, AssetClassID cls, byte[] bytes)
    {
        if (written.FirstOrDefault(w => w.Format == Replaceable.RawFormat) is { } already)
            return already.Path;

        var stem = written.Count > 0
            ? Path.Combine(Path.GetDirectoryName(written[0].Path)!,
                Path.GetFileNameWithoutExtension(written[0].Path))
            : Path.Combine(outputDirectory, AssetExporter.Sanitize(fileName ?? source.Name));

        var path = stem + "." + Replaceable.RawFormat;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static IReadOnlyList<ExportedAsset> Combine(IReadOnlyList<ExportedAsset> written, string? raw)
    {
        if (raw is null || written.Any(w => w.Path == raw)) return written;

        var like = written[0];
        return [.. written, like with
        {
            Path = raw,
            Format = Replaceable.RawFormat,
            Bytes = new FileInfo(raw).Length,
        }];
    }
}
