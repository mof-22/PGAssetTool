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

    /// Which way up the model is looked at. How far away is worked out per model rather than fixed.
    public static Camera Angle { get; } = new(Yaw: 0.7f, Pitch: 0.35f);

    /// How much of the frame the model is made to take up, leaving a margin so it does not read as
    /// a crop of something larger.
    private const float Fill = 0.94f;

    /// Draws the model as large as it will go, at the angle it was asked for.
    ///
    /// The distance the preview starts at is a multiple of the model's bounding sphere, which for
    /// a rifle is most of a metre of empty air in every direction that is not along the barrel — so
    /// a fixed distance drew a small object in a large empty square. Where it actually lands is
    /// measured from the drawing and the framing corrected, which needs no knowledge of the shape
    /// and holds at any angle.
    public static PreviewImage Render(
        UnityMesh mesh, IReadOnlyList<PreviewImage?>? textures, Camera? camera = null, int size = Size)
    {
        var target = new RenderTarget();
        target.Resize(size, size);
        var view = camera ?? Angle;

        MeshRenderer.Render(mesh, view, target, textures);

        // Twice: the first correction is computed from a drawing that was not framed yet, and a
        // long model swinging into the corners moves further than a linear guess allows for.
        for (var pass = 0; pass < 2 && Drawn(target) is { } bounds; pass++)
        {
            view = Frame(view, bounds, size);
            MeshRenderer.Render(mesh, view, target, textures);
        }

        // The rasterizer writes an opaque pixel where it draws and leaves the rest at zero, so what
        // it produces is already straight alpha and needs no unpicking.
        return new PreviewImage(size, size, target.Bgra);
    }

    /// Moves the drawing to the middle of the frame and scales it to fill.
    private static Camera Frame(Camera camera, (int Left, int Top, int Right, int Bottom) drawn, int size)
    {
        var half = size * 0.5f;
        var middleX = (drawn.Left + drawn.Right + 1) * 0.5f;
        var middleY = (drawn.Top + drawn.Bottom + 1) * 0.5f;

        // Panned takes half-frames, rightwards and upwards; the screen counts rows downwards.
        var centred = camera.Panned((half - middleX) / half, (middleY - half) / half);

        // The wider of the two extents decides, or a long model would be scaled to fit its height
        // and hang off both sides.
        var extent = Math.Max(drawn.Right - drawn.Left + 1, drawn.Bottom - drawn.Top + 1) / (float)size;
        if (extent <= 0) return centred;

        return centred with { Distance = Math.Clamp(camera.Distance * extent / Fill, 0.05f, 20f) };
    }

    /// The box the drawing occupies, or null when nothing was drawn.
    private static (int Left, int Top, int Right, int Bottom)? Drawn(RenderTarget target)
    {
        int left = target.Width, top = target.Height, right = -1, bottom = -1;

        for (var y = 0; y < target.Height; y++)
            for (var x = 0; x < target.Width; x++)
            {
                if (target.Bgra[(y * target.Width + x) * 4 + 3] == 0) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }

        return right < left ? null : (left, top, right, bottom);
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
