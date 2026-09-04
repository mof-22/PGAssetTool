using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Fmod5Sharp;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Export;

public sealed record ExportedAsset(
    string Path, AssetClassID Class, string Name, string Format, long Bytes, AssetAddress Address);

/// Writes assets out in whatever format is actually editable for their type: an image editor can
/// open a PNG, an audio editor a WAV. Types with no such format fall back to a readable field dump
/// plus the raw serialized bytes.
public sealed class AssetExporter(BundleSet bundles)
{
    /// The per-type writers do not know the asset's address; Export fills it in on the way out.
    private static readonly AssetAddress Placeholder = new("", "", "");

    public ContainerIndex Index { get; } = new(bundles.Context);

    /// `fileNameOverride` keeps distinct assets that share a name from overwriting each other's
    /// output; the caller knows which names repeat and can qualify them. `sourceBytes` is the data
    /// the asset was actually read from, which differs from what the bundle holds when the caller
    /// has staged a replacement over it.
    public IReadOnlyList<ExportedAsset> Export(
        string bundle, AssetsFileInstance file, AssetFileInfo info, string directory,
        string? fileNameOverride = null, byte[]? sourceBytes = null)
    {
        Directory.CreateDirectory(directory);
        var field = bundles.Context.Deserialize(file, info);
        var cls = (AssetClassID)info.TypeId;
        var named = field?["m_Name"] is { IsDummy: false } n && n.AsString.Length > 0 ? n.AsString : null;
        var name = named ?? $"{cls}_{info.PathId}";
        var address = Index.AddressOf(bundle, file, info, named ?? "");
        var stem = Path.Combine(directory, Sanitize(fileNameOverride ?? name));

        if (field is not null)
        {
            var exported = cls switch
            {
                AssetClassID.Texture2D => ExportTexture(bundle, field, stem),
                AssetClassID.AudioClip => ExportAudio(bundle, field, stem),
                _ => null,
            };
            if (exported is not null) return [exported with { Class = cls, Name = name, Address = address }];
        }

        // No editable interchange format for this type, so dump the fields. Whether that dump holds
        // everything is checked rather than assumed: if writing the parsed asset back reproduces the
        // bytes it came from, nothing was lost and the raw copy would only repeat it. If it does
        // not, the raw bytes are kept so the difference is never silently discarded.
        var raw = sourceBytes ?? ReadRaw(file, info);
        var results = new List<ExportedAsset>();
        if (field is not null)
        {
            var jsonPath = stem + ".json";
            File.WriteAllText(jsonPath, FieldDump.ToJson(field));
            results.Add(new ExportedAsset(jsonPath, cls, name, "json", new FileInfo(jsonPath).Length, address));
            if (Reproduces(field, raw)) return results;
        }

        var rawPath = stem + ".dat";
        File.WriteAllBytes(rawPath, raw);
        results.Add(new ExportedAsset(rawPath, cls, name, "dat", new FileInfo(rawPath).Length, address));
        return results;
    }

    /// A raw asset can carry slack past the object — an editor that wrote a smaller asset into a
    /// buffer sized for a larger one leaves the old tail behind — so matching the leading bytes is
    /// what completeness means here.
    private static bool Reproduces(AssetTypeValueField field, byte[] source)
    {
        try
        {
            var written = field.WriteToByteArray();
            return written.Length <= source.Length && source.AsSpan(0, written.Length).SequenceEqual(written);
        }
        catch
        {
            return false;
        }
    }

    private ExportedAsset? ExportTexture(string bundle, AssetTypeValueField field, string stem)
    {
        var texture = TextureFile.ReadTextureFile(field);
        var pixels = ResolvePayload(bundle, field["m_StreamData"], texture.pictureData);
        if (pixels is null || pixels.Length == 0) return null;

        var path = stem + ".png";
        texture.pictureData = pixels;
        if (!texture.DecodeTextureImage(pixels, path, ImageExportType.Png, 100)) return null;
        return new ExportedAsset(path, AssetClassID.Texture2D, "", "png", new FileInfo(path).Length, Placeholder);
    }

    private ExportedAsset? ExportAudio(string bundle, AssetTypeValueField field, string stem)
    {
        var resource = field["m_Resource"];
        if (resource.IsDummy) return null;
        var payload = bundles.ReadResource(
            bundle, resource["m_Source"].AsString, resource["m_Offset"].AsLong, resource["m_Size"].AsLong);

        if (!FsbLoader.TryLoadFsbFromByteArray(payload, out var bank) || bank is null) return null;
        var sample = bank.Samples.FirstOrDefault();
        if (sample is null || !sample.RebuildAsStandardFileFormat(out var data, out var extension)) return null;

        var path = $"{stem}.{extension}";
        File.WriteAllBytes(path, data!);
        return new ExportedAsset(path, AssetClassID.AudioClip, "", extension!, data!.Length, Placeholder);
    }

    /// Payload bytes either sit inline on the object or in a stream entry it points at.
    private byte[]? ResolvePayload(string bundle, AssetTypeValueField streamData, byte[]? inline)
    {
        if (streamData.IsDummy) return inline;
        var source = streamData["path"].AsString;
        if (source.Length == 0) return inline;
        return bundles.ReadResource(
            bundle, source, streamData["offset"].AsLong, streamData["size"].AsLong);
    }

    private static byte[] ReadRaw(AssetsFileInstance file, AssetFileInfo info)
    {
        var reader = file.file.Reader;
        reader.Position = info.GetAbsoluteByteOffset(file.file);
        return reader.ReadBytes((int)info.ByteSize);
    }

    public static string Sanitize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < name.Length; i++)
            buffer[i] = invalid.Contains(name[i]) ? '_' : name[i];
        return new string(buffer);
    }
}
