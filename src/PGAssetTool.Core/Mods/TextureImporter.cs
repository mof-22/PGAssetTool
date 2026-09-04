using AssetsTools.NET;
using AssetsTools.NET.Texture;

namespace PGAssetTool.Core.Mods;

public sealed record TextureChange(
    TextureFormat OriginalFormat,
    TextureFormat WrittenFormat,
    int OldWidth,
    int OldHeight,
    int NewWidth,
    int NewHeight)
{
    public bool Resized => OldWidth != NewWidth || OldHeight != NewHeight;
    public bool Recoded => OriginalFormat != WrittenFormat;

    public override string ToString()
    {
        var format = Recoded ? $"{OriginalFormat} -> {WrittenFormat}" : OriginalFormat.ToString();
        var size = Resized ? $"{OldWidth}x{OldHeight} -> {NewWidth}x{NewHeight}" : $"{NewWidth}x{NewHeight}";
        return $"{format} {size}";
    }
}

public static class TextureImporter
{
    /// The encoder handles uncompressed layouts only; every block-compressed format comes back
    /// empty. Since 46% of the game's weapon textures are BC7, that is the common case, not an edge.
    public const TextureFormat Fallback = TextureFormat.RGBA32;

    /// Replaces a Texture2D's pixels with an image file.
    ///
    /// The original format is kept when it can be written. When it cannot, the texture is written
    /// uncompressed rather than refused. That is not a compromise on quality: re-encoding the
    /// author's artwork into a lossy block format would add compression artifacts that were never
    /// in it, and avoiding exactly that was the reason for keeping the original format in the first
    /// place. It costs size, and the original bytes stay in the stream file regardless.
    public static TextureChange Replace(AssetTypeValueField field, string imagePath)
    {
        var texture = TextureFile.ReadTextureFile(field);
        var original = (TextureFormat)texture.m_TextureFormat;
        int oldWidth = texture.m_Width, oldHeight = texture.m_Height;

        var written = original;
        if (!TryEncode(texture, imagePath))
        {
            texture.m_TextureFormat = (int)Fallback;
            written = Fallback;
            if (!TryEncode(texture, imagePath))
                throw new NotSupportedException(
                    $"'{texture.m_Name}' could not be written as {original} or as {Fallback}.");
        }

        // A replaced texture has no mip chain, and claiming one would send the game reading past
        // the data.
        texture.m_MipCount = 1;
        texture.m_MipMap = false;
        texture.WriteTo(field);

        // The pixels are inline now, so the pointer into the shared stream file has to go: left in
        // place it would send the game back to the original bytes.
        var streamData = field["m_StreamData"];
        if (!streamData.IsDummy)
        {
            streamData["path"].AsString = "";
            streamData["offset"].AsULong = 0;
            streamData["size"].AsUInt = 0;
        }

        return new TextureChange(original, written, oldWidth, oldHeight, texture.m_Width, texture.m_Height);
    }

    private static bool TryEncode(TextureFile texture, string imagePath)
    {
        try
        {
            texture.EncodeTextureImage(imagePath, 100);
            return texture.pictureData is { Length: > 0 };
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
