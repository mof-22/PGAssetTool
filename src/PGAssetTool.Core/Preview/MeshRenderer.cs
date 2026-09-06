using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// How the model is being looked at. Angles are radians; distance is a multiple of the model's own
/// radius, so a pistol and a rocket launcher both start out filling the frame.
/// <param name="PivotX">
/// The point of the model held at the middle of the frame, offset from the model's own centre and
/// measured in model radii. Zero is the centre of the model, which is where a view starts.
///
/// This is what turning happens around, so panning something to the middle and then turning keeps
/// it there — pan the muzzle of a rocket launcher into view, turn, and the muzzle stays in view.
/// Holding the offset in the frame instead would have turned the model about its own centre and
/// swung whatever was being looked at straight back out of the frame.
///
/// In radii rather than in model units so it means the same thing on a pistol and a launcher, and
/// so a resized pane keeps the framing rather than throwing it away.
/// </param>
public sealed record Camera(
    float Yaw = 0.7f, float Pitch = 0.35f, float Distance = 1.5f, float Roll = 0f,
    float PivotX = 0f, float PivotY = 0f, float PivotZ = 0f)
{
    /// <param name="dx">Rightwards, in half-frames: 1 moves the model a half-frame to the right.</param>
    /// <param name="dy">Upwards, in the same units.</param>
    public Camera Panned(float dx, float dy)
    {
        // The cursor drags the model, so the point being held moves the other way. A half-frame is
        // Distance radii across, which is what turns the drag into the units the pivot is kept in.
        var view = MeshRenderer.View(this);
        var (right, up) = (-dx * Distance, -dy * Distance);

        return this with
        {
            PivotX = PivotX + view.Rx * right + view.Ux * up,
            PivotY = PivotY + view.Ry * right + view.Uy * up,
            PivotZ = PivotZ + view.Rz * right + view.Uz * up,
        };
    }

    /// Turns the model by a drag across the screen: rightwards and downwards, in radians.
    ///
    /// The drag is taken out of the roll before it is used. Yaw goes about the world's up axis and
    /// pitch about the camera's right, and a rolled view has neither of those lying along the screen
    /// any more — so feeding the drag straight in meant that after a quarter turn of tilt, dragging
    /// up span the model sideways. Undoing the roll first asks the question the drag actually means:
    /// which way did the cursor go across the picture as it now stands.
    public Camera Dragged(float right, float down)
    {
        var (c, s) = (MathF.Cos(Roll), MathF.Sin(Roll));
        return Turned(-(c * right + s * down), -(s * right - c * down));
    }

    public Camera Turned(float dYaw, float dPitch) => this with
    {
        Yaw = Yaw + dYaw,

        // Stop just short of the poles: at exactly straight up the view direction and the up vector
        // are parallel and the frame cannot be built.
        Pitch = Math.Clamp(Pitch + dPitch, -1.55f, 1.55f),
    };

    /// Tilts the model in the plane of the screen.
    ///
    /// Yaw and pitch orbit the camera, which covers two of the three ways an object can be turned;
    /// this is the third. Without it a weapon can be looked at from any side but never straightened,
    /// and the automatic uprighting has to be right for every model in the game because nothing can
    /// correct it by hand.
    public Camera Rolled(float dRoll) => this with { Roll = Roll + dRoll };

    public Camera Zoomed(float factor) => this with { Distance = Math.Clamp(Distance * factor, 0.4f, 20f) };
}

/// Draws a mesh into a pixel buffer, in software.
///
/// Avalonia has no 3D of its own, and reaching for OpenGL would trade a few hundred lines for a
/// dependency on whatever driver the machine happens to have. These meshes are tiny — the largest
/// weapon in the game is a few thousand triangles — so a plain z-buffered rasterizer redraws well
/// inside a frame and cannot fail to initialise.
/// The buffers a preview draws into, kept across frames.
///
/// The depth buffer is the size of the frame, and allocating one per frame is what made a large
/// preview pane expensive: eleven megabytes a frame at 2000x1500, all of it immediately garbage.
/// Held here, a frame allocates nothing at all.
public sealed class RenderTarget
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// Premultiplied BGRA, one row after another, ready to hand to a bitmap.
    public byte[] Bgra { get; private set; } = [];

    internal float[] Depth { get; private set; } = [];

    public bool IsEmpty => Width < 1 || Height < 1;

    public void Resize(int width, int height)
    {
        if (Width == width && Height == height) return;

        (Width, Height) = (width, height);
        var pixels = Math.Max(width * height, 0);
        Bgra = new byte[pixels * 4];
        Depth = new float[pixels];
    }
}

public static class MeshRenderer
{
    /// <param name="textures">
    /// One per submesh, in Unity's order: submesh <c>i</c> is drawn with the texture of material
    /// <c>i</c>. A null entry, or a mesh with more submeshes than textures, falls back to plain
    /// shading rather than to whatever texture happened to be first.
    /// </param>
    public static void Render(
        UnityMesh mesh, Camera camera, RenderTarget target, IReadOnlyList<PreviewImage?>? textures = null)
    {
        if (target.IsEmpty) return;

        var (bgra, width, height) = (target.Bgra, target.Width, target.Height);
        Array.Clear(bgra);

        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return;

        var normals = mesh.Get(VertexAttribute.Normal);
        var uvs = mesh.Get(VertexAttribute.TexCoord0);
        var (centre, size, radius) = Bounds(mesh, positions);
        var upright = Upright.For(size);
        if (radius <= 0) radius = 1;

        var view = View(camera);
        // Distance is literally how many model radii the half-frame covers, so 1.5 leaves a margin.
        var scale = Math.Min(width, height) * 0.5f / (radius * camera.Distance);

        // Whatever the pivot names is what sits in the middle of the frame, and so what turning
        // happens around. Subtracted from the model rather than added to the drawing, which is the
        // whole difference: the other way turns the model about its own centre and swings whatever
        // was panned into view straight back out of it.
        var (pivotX, pivotY, pivotZ) =
            (camera.PivotX * radius, camera.PivotY * radius, camera.PivotZ * radius);

        var depth = target.Depth;
        Array.Fill(depth, float.NegativeInfinity);

        Span<float> sx = stackalloc float[3];
        Span<float> sy = stackalloc float[3];
        Span<float> sz = stackalloc float[3];
        Span<float> shade = stackalloc float[3];
        Span<float> u = stackalloc float[3];
        Span<float> v = stackalloc float[3];

        // Submesh by submesh, because which material draws a triangle is decided by which submesh
        // it belongs to. A mesh with no submeshes recorded is drawn whole.
        var parts = mesh.SubMeshes.Count > 0
            ? mesh.SubMeshes
            : [new SubMesh(0, mesh.Indices.Length, 0, 0)];

        for (var part = 0; part < parts.Count; part++)
        {
            var texture = textures is not null && part < textures.Count ? textures[part] : null;
            var from = Math.Max(parts[part].IndexStart, 0);
            var to = Math.Min(from + parts[part].IndexCount, mesh.Indices.Length);

            for (var i = from; i + 2 < to + 1 && i + 2 < mesh.Indices.Length; i += 3)
            {
                var ok = true;
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = mesh.Indices[i + corner];
                    if (vertex < 0 || vertex >= mesh.VertexCount) { ok = false; break; }

                    var (mx, my, mz) = upright.Apply(
                        positions[vertex * 3] - centre.X,
                        positions[vertex * 3 + 1] - centre.Y,
                        positions[vertex * 3 + 2] - centre.Z);
                    var (x, y, z) = view.Apply(mx - pivotX, my - pivotY, mz - pivotZ);

                    sx[corner] = width * 0.5f + x * scale;
                    sy[corner] = height * 0.5f - y * scale;
                    sz[corner] = z;

                    shade[corner] = normals is null ? 1f : Lambert(view, upright, normals, vertex);
                    u[corner] = uvs is null ? 0 : uvs[vertex * 2];
                    v[corner] = uvs is null ? 0 : uvs[vertex * 2 + 1];
                }
                if (!ok) continue;

                Fill(bgra, depth, width, height, sx, sy, sz, shade, u, v, texture);
            }
        }
    }

    /// A single light over the viewer's shoulder, with enough ambient that faces turned away stay
    /// readable instead of going black.
    private static float Lambert(Basis view, Upright upright, float[] normals, int vertex)
    {
        var (ux, uy, uz) = upright.Apply(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
        var (nx, ny, nz) = view.Apply(ux, uy, uz);
        var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length <= 0) return 1f;

        // The light sits up and to the left of the camera, in view space.
        var lambert = (nx * -0.35f + ny * 0.5f + nz * 0.79f) / length;
        return 0.25f + 0.75f * Math.Max(lambert, 0f);
    }

    private static void Fill(
        byte[] bgra, float[] depth, int width, int height,
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz, ReadOnlySpan<float> shade,
        ReadOnlySpan<float> u, ReadOnlySpan<float> v, PreviewImage? texture)
    {
        var area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
        if (Math.Abs(area) < 1e-6f) return;

        var minX = Math.Max((int)MathF.Floor(Math.Min(sx[0], Math.Min(sx[1], sx[2]))), 0);
        var maxX = Math.Min((int)MathF.Ceiling(Math.Max(sx[0], Math.Max(sx[1], sx[2]))), width - 1);
        var minY = Math.Max((int)MathF.Floor(Math.Min(sy[0], Math.Min(sy[1], sy[2]))), 0);
        var maxY = Math.Min((int)MathF.Ceiling(Math.Max(sy[0], Math.Max(sy[1], sy[2]))), height - 1);

        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var px = x + 0.5f;
            var py = y + 0.5f;

            // Barycentric coordinates, normalised by the signed area so the sign of the triangle
            // does not matter — back faces are drawn too, since these meshes are not all closed.
            var w0 = ((sx[1] - px) * (sy[2] - py) - (sx[2] - px) * (sy[1] - py)) / area;
            var w1 = ((sx[2] - px) * (sy[0] - py) - (sx[0] - px) * (sy[2] - py)) / area;
            var w2 = 1f - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;

            var z = w0 * sz[0] + w1 * sz[1] + w2 * sz[2];
            var at = y * width + x;
            if (z <= depth[at]) continue;
            depth[at] = z;

            var lit = Math.Clamp(w0 * shade[0] + w1 * shade[1] + w2 * shade[2], 0f, 1f);

            byte blue = 240, green = 240, red = 240;
            if (texture is not null)
                Sample(texture, w0 * u[0] + w1 * u[1] + w2 * u[2], w0 * v[0] + w1 * v[1] + w2 * v[2],
                    out blue, out green, out red);

            // Ambient floor so a face turned away stays readable rather than going black.
            bgra[at * 4] = (byte)(blue * (0.17f + 0.83f * lit));
            bgra[at * 4 + 1] = (byte)(green * (0.17f + 0.83f * lit));
            bgra[at * 4 + 2] = (byte)(red * (0.17f + 0.83f * lit));
            bgra[at * 4 + 3] = 255;
        }
    }

    /// Nearest texel, deliberately. Every texture in this game is small and hand-drawn — 256x256 at
    /// the largest, most of them 64 or 128 — and smoothing them would show something the game never
    /// does. Coordinates wrap, because a mesh is free to use them outside the unit square.
    private static void Sample(PreviewImage texture, float u, float v, out byte blue, out byte green, out byte red)
    {
        var x = (int)MathF.Floor(Wrap(u) * texture.Width);
        // Unity puts the texture origin at the bottom left; the decoded rows run top down.
        var y = (int)MathF.Floor((1f - Wrap(v)) * texture.Height);

        x = Math.Clamp(x, 0, texture.Width - 1);
        y = Math.Clamp(y, 0, texture.Height - 1);

        var at = (y * texture.Width + x) * 4;
        (blue, green, red) = (texture.Bgra[at], texture.Bgra[at + 1], texture.Bgra[at + 2]);
    }

    private static float Wrap(float value)
    {
        var fraction = value - MathF.Floor(value);
        return fraction is >= 0 and < 1 ? fraction : 0;
    }

    /// Stands the model up before the camera looks at it: longest side across the screen, next
    /// longest up it.
    ///
    /// A Mesh asset carries no orientation. In the game it is placed by the transform of whichever
    /// renderer draws it, and a preview showing one on its own has nothing to place it with — so
    /// weapons, whose barrels are authored along Y, arrive pointing straight down. Sorting the
    /// bounding box turns that into a level, side-on view without needing to know what the model is.
    ///
    /// Which end the muzzle is on is not recoverable this way. The obvious guess, that the thinner
    /// end is the barrel, holds for only 46 of the 73 clearly-long meshes in one bundle, with the
    /// margins inside the noise — so it is not guessed at, and a gun may face either way.
    private readonly record struct Upright(int Right, int Up, int Depth, float Flip)
    {
        public (float X, float Y, float Z) Apply(float x, float y, float z)
        {
            Span<float> v = [x, y, z];
            return (v[Right], v[Up], v[Depth] * Flip);
        }

        public static Upright For((float X, float Y, float Z) size)
        {
            Span<float> extents = [size.X, size.Y, size.Z];
            Span<int> order = [0, 1, 2];

            // Three elements, so a pair of passes settles it and a sort is not worth reaching for.
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < 2; i++)
                    if (extents[order[i]] < extents[order[i + 1]])
                        (order[i], order[i + 1]) = (order[i + 1], order[i]);

            // Reordering axes can mirror the model. An odd permutation is put back by flipping
            // depth, which leaves the silhouette alone and only decides which side is towards you.
            var odd = (order[0], order[1]) is (0, 2) or (1, 0) or (2, 1);
            return new Upright(order[0], order[1], order[2], odd ? -1f : 1f);
        }
    }

    /// The camera's axes, as three rows that turn a model-space vector into view space.
    internal readonly record struct Basis(
        float Rx, float Ry, float Rz, float Ux, float Uy, float Uz, float Fx, float Fy, float Fz)
    {
        public (float X, float Y, float Z) Apply(float x, float y, float z)
            => (Rx * x + Ry * y + Rz * z, Ux * x + Uy * y + Uz * z, Fx * x + Fy * y + Fz * z);
    }

    internal static Basis View(Camera camera)
    {
        var (cy, sy) = (MathF.Cos(camera.Yaw), MathF.Sin(camera.Yaw));
        var (cp, sp) = (MathF.Cos(camera.Pitch), MathF.Sin(camera.Pitch));

        // Forward points from the model towards the camera, so a larger Z is *nearer* the viewer and
        // the depth test keeps the largest.
        var (fx, fy, fz) = (cp * sy, sp, cp * cy);
        var (rx, ry, rz) = (cy, 0f, -sy);
        var (ux, uy, uz) = (fy * rz - fz * ry, fz * rx - fx * rz, fx * ry - fy * rx);

        // Roll turns the frame about the direction it already looks along, so the model tilts in
        // the plane of the screen and nothing about which side is facing you changes.
        if (camera.Roll != 0)
        {
            var (cr, sr) = (MathF.Cos(camera.Roll), MathF.Sin(camera.Roll));
            (rx, ry, rz, ux, uy, uz) = (
                rx * cr + ux * sr, ry * cr + uy * sr, rz * cr + uz * sr,
                ux * cr - rx * sr, uy * cr - ry * sr, uz * cr - rz * sr);
        }

        return new Basis(rx, ry, rz, ux, uy, uz, fx, fy, fz);
    }

    private static ((float X, float Y, float Z) Centre, (float X, float Y, float Z) Size, float Radius)
        Bounds(UnityMesh mesh, float[] positions)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            minX = Math.Min(minX, positions[v * 3]); maxX = Math.Max(maxX, positions[v * 3]);
            minY = Math.Min(minY, positions[v * 3 + 1]); maxY = Math.Max(maxY, positions[v * 3 + 1]);
            minZ = Math.Min(minZ, positions[v * 3 + 2]); maxZ = Math.Max(maxZ, positions[v * 3 + 2]);
        }

        var centre = ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        var size = (maxX - minX, maxY - minY, maxZ - minZ);
        var radius = MathF.Sqrt(
            MathF.Pow(size.Item1 / 2, 2) + MathF.Pow(size.Item2 / 2, 2) + MathF.Pow(size.Item3 / 2, 2));
        return (centre, size, radius);
    }
}
