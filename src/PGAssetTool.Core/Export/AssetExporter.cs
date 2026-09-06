using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Fmod5Sharp;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Import.Audio;

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

    /// Write textures with no alpha channel at all. See WriteWithoutAlpha for why anyone would.
    public bool Opaque { get; init; }

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
                AssetClassID.Mesh => ExportMesh(field, stem),
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

        if (Opaque)
        {
            if (!WriteWithoutAlpha(texture, pixels, path)) return null;
        }
        else if (!texture.DecodeTextureImage(pixels, path, ImageExportType.Png, 100)) return null;

        return new ExportedAsset(path, AssetClassID.Texture2D, "", "png", new FileInfo(path).Length, Placeholder);
    }

    /// Writes the colour channels and drops the alpha entirely.
    ///
    /// Most of these textures keep something other than coverage in that channel — emission,
    /// usually — so an image editor opens them as mostly-transparent and painting means fighting a
    /// mask that has nothing to do with what is being painted. The alpha is not lost: an image
    /// brought back without one is given the original's again.
    private static bool WriteWithoutAlpha(TextureFile texture, byte[] pixels, string path)
    {
        var bgra = texture.DecodeTextureRaw(pixels, useBgra: true);
        if (bgra is null || bgra.Length < texture.m_Width * texture.m_Height * 4) return false;

        var rgb = new byte[texture.m_Width * texture.m_Height * 3];
        var stride = texture.m_Width * 4;

        for (var row = 0; row < texture.m_Height; row++)
        {
            // Unity stores the bottom row first; a PNG starts at the top.
            var from = (texture.m_Height - 1 - row) * stride;
            var to = row * texture.m_Width * 3;
            for (var x = 0; x < texture.m_Width; x++)
            {
                rgb[to + x * 3] = bgra[from + x * 4 + 2];
                rgb[to + x * 3 + 1] = bgra[from + x * 4 + 1];
                rgb[to + x * 3 + 2] = bgra[from + x * 4];
            }
        }

        using var file = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgb, texture.m_Width, texture.m_Height, StbImageWriteSharp.ColorComponents.RedGreenBlue, file);
        return true;
    }

    /// A mesh that cannot be unpacked falls through to the field dump rather than failing the export.
    private static ExportedAsset? ExportMesh(AssetTypeValueField field, string stem)
    {
        try
        {
            var mesh = UnityMesh.Read(field);
            if (mesh.VertexCount == 0) return null;
            var path = stem + ".glb";
            GlbWriter.Write(mesh, path);
            return new ExportedAsset(path, AssetClassID.Mesh, "", "glb", new FileInfo(path).Length, Placeholder);
        }
        catch (NotSupportedException)
        {
            return null;
        }
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

        // The rebuilt WAV declares neither its RIFF length nor its data length; see WaveFile.
        if (extension == "wav") data = WaveFile.WithLengthsFilledIn(data!);

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
