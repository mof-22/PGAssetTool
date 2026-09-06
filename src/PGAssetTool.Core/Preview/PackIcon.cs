using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// The picture a pack shows itself with: its model, wearing its textures, drawn once to a file.
///
/// The game's own weapon icon says which weapon a pack is for and nothing about what the pack does
/// to it — which is the only interesting part, since these are texture and mesh mods. A drawing of
/// the thing itself answers both at a glance.
///
/// Drawn to a file rather than rendered wherever it is shown. A manager listing a dozen mods would
/// otherwise rasterise a dozen models on every repaint, and the angle would be whatever the code
/// chose rather than the one the author thought showed their work best.
public static class PackIcon
{
    /// Written into the workspace under a name the export never produces, so it can never be
    /// mistaken for one of the weapon's own files or picked up as something to replace.
    public const string FileName = "pack-icon.png";

    public const int Size = 256;

    /// A little further back than the preview starts at, since an icon is looked at small and a
    /// model touching the edges reads as a crop rather than as an object.
    public static Camera Angle { get; } = new(Yaw: 0.7f, Pitch: 0.35f, Distance: 1.75f);

    public static PreviewImage Render(
        UnityMesh mesh, IReadOnlyList<PreviewImage?>? textures, Camera? camera = null, int size = Size)
    {
        var target = new RenderTarget();
        target.Resize(size, size);
        MeshRenderer.Render(mesh, camera ?? Angle, target, textures);

        // The rasterizer writes an opaque pixel where it draws and leaves the rest at zero, so what
        // it produces is already straight alpha and needs no unpicking.
        return new PreviewImage(size, size, target.Bgra);
    }

    /// True when anything was actually drawn. A model that missed the frame entirely would write a
    /// file that is transparent from corner to corner, which looks exactly like a missing icon.
    public static bool IsBlank(PreviewImage picture)
    {
        for (var i = 3; i < picture.Bgra.Length; i += 4)
            if (picture.Bgra[i] != 0) return false;
        return true;
    }

    public static void Write(PreviewImage picture, string path)
    {
        var rgba = new byte[picture.Bgra.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = picture.Bgra[i + 2];
            rgba[i + 1] = picture.Bgra[i + 1];
            rgba[i + 2] = picture.Bgra[i];
            rgba[i + 3] = picture.Bgra[i + 3];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(
            rgba, picture.Width, picture.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, file);
    }
}
