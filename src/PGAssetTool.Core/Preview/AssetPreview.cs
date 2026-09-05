using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// Decoded pixels, ready to hand to a bitmap. Rows run top to bottom; Unity stores them the other
/// way up and this flips them, because every image API expects the first row to be the top one.
public sealed record PreviewImage(int Width, int Height, byte[] Bgra)
{
    public int Stride => Width * 4;
}

/// Reads an asset into something that can be shown, without going through a file.
///
/// The exporter writes PNG and glTF because that is what an editor opens. A preview needs neither —
/// it needs pixels and triangles in memory — so the decoding is shared but the writing is not.
public static class AssetPreview
{
    public static PreviewImage? Texture(BundleSet bundles, string bundle, AssetTypeValueField field)
    {
        var texture = TextureFile.ReadTextureFile(field);

        // Nearly every texture in the game keeps its pixels in a sibling .resS rather than on the
        // object, so the payload has to be fetched before anything can be decoded.
        var payload = texture.pictureData;
        var stream = field["m_StreamData"];
        if (!stream.IsDummy && stream["path"].AsString.Length > 0)
            payload = bundles.ReadResource(
                bundle, stream["path"].AsString, stream["offset"].AsLong, stream["size"].AsLong);

        if (payload is null || payload.Length == 0) return null;

        var bgra = texture.DecodeTextureRaw(payload, useBgra: true);
        if (bgra is null || bgra.Length < texture.m_Width * texture.m_Height * 4) return null;

        return new PreviewImage(texture.m_Width, texture.m_Height, FlipRows(bgra, texture.m_Width, texture.m_Height));
    }

    public static UnityMesh? Mesh(AssetTypeValueField field)
    {
        try
        {
            var mesh = UnityMesh.Read(field);
            return mesh.VertexCount > 0 ? mesh : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] FlipRows(byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var flipped = new byte[stride * height];
        for (var row = 0; row < height; row++)
            Array.Copy(bgra, row * stride, flipped, (height - 1 - row) * stride, stride);
        return flipped;
    }
}
