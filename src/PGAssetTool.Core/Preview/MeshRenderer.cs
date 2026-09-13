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
/// <param name="Distance">
/// How many of the model's own radii the half-frame covers. One puts the bounding sphere exactly
/// inside the shorter side of the pane, which is as close as a model can be framed without a
/// corner of it going off the edge at some angle.
/// </param>
/// <param name="Yaw">
/// The defaults for this, the pitch and the roll are the angle the game draws its own `icon1_big`
/// shop pictures at, so an author opening a weapon meets the shape they already know.
///
/// Measured rather than chosen. Fitting the silhouette to the icons never rose above about 0.8 and
/// never sharply — the icons carry a drawn outline that fattens their shape — so the author matched
/// 55 weapons by hand and the angles were read back against each frame the tool might stand a model
/// up in. Against the bounding box with the muzzle faced they agree to within 8 degrees; against the
/// bounding box alone the yaw spreads nearly three times as far, which is what facing is worth.
///
/// The 39 that fire agree to 4.6 degrees of yaw. The 16 that do not spread over 35 and sit around
/// the same middle: a blade has no convention rather than a different one, so there is one opening
/// angle and not two, and a knife will not match its own icon whatever is done.
/// </param>
/// <param name="Roll">
/// Not zero by default, which looks like a mistake and is not: every icon in the game lies along a
/// diagonal with the muzzle up and to the right.
/// </param>
public sealed record Camera(
    float Yaw = -0.794f, float Pitch = -0.314f, float Distance = 1f, float Roll = 0.271f,
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
    /// Sideways is always the turntable's own axis and up-and-down is always the pitch, whatever
    /// the view is tilted to. That is what a turntable is: the platter turns about one axis that
    /// does not move, and tilting your head does not change which axis that is.
    ///
    /// The drag used to be taken out of the roll first and split between yaw and pitch, so that it
    /// followed the picture as it stood. It reads well for a small drag and it does not hold
    /// together: yaw and pitch do not commute, so the same drag applied in pieces does not arrive
    /// where it does applied whole, and the model wanders as you work it. Once the pitch was stopped
    /// at the poles it became plainly wrong — a sideways drag turned partly into a pitch, ran into
    /// the stop, and what was left of it went on turning the model about the vertical while the
    /// cursor moved horizontally. Nothing about that is recoverable by adjusting the mixture.
    ///
    /// Sideways and yaw agree in sign because the picture is no longer mirrored: dragging right
    /// walks the viewer round towards the model's own right, which is the side of the screen that
    /// side is now drawn on.
    public Camera Dragged(float right, float down) => Turned(right, down);

    /// Pitch stops at straight up and straight down, which is what makes this a turntable.
    ///
    /// It did not, for a while: the frame is square at every pitch — screen-right comes from the
    /// yaw alone, so there is no pole for it to collapse at — and carrying on over the top looked
    /// like something gained for nothing. What it cost was the two things anybody actually
    /// noticed. Past the top the frame's up vector is inverted, so a drag to the right walked the
    /// viewer left; and a drag on a rolled view is taken apart into yaw and pitch, which past the
    /// top puts the pieces back in the wrong places and the model tumbles end over end.
    ///
    /// Nothing is out of reach for the stopping. Above and below are both inside a quarter turn
    /// either way, and going over the top only ever arrived at a view already reachable by
    /// dragging the other way — upside down.
    ///
    /// Exactly at the pole rather than short of it: the frame is well defined there, so there is
    /// no reason for the wall to stand two degrees inside the thing it is protecting.
    public Camera Turned(float dYaw, float dPitch) => this with
    {
        Yaw = Yaw + dYaw,
        Pitch = Math.Clamp(Pitch + dPitch, -MathF.PI / 2, MathF.PI / 2),
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

/// The two rotations between a model's own vertices and the screen, for a caller that wants to
/// decide them itself rather than take the ones a <see cref="Camera"/> implies.
///
/// <c>Standing</c> stands the model up before anybody looks at it; the renderer's own answer is the
/// bounding box sorted by extent, which knows nothing about which way a gun points — see
/// <see cref="Facing"/>, which does. <c>View</c> is where the viewer is standing.
///
/// Both are ordinary rotations, so a caller with its own answer to either draws through the same
/// rasterizer as everything else instead of a copy of it. Distance and pivot still come from the
/// camera.
public readonly record struct Viewpoint(MeshRenderer.Basis Standing, MeshRenderer.Basis View);

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
    /// <param name="framing">
    /// The model to size and centre the view by, when that is not the one being drawn.
    ///
    /// An animation moves vertices, and a frame worked out from where they are now moves with them:
    /// a pistol whose magazine drops out of it is drawn smaller and lower for as long as the
    /// magazine is out, so what an author sees is the whole gun sliding about rather than the part
    /// that is actually moving. Framing by the model at rest holds the camera still.
    /// </param>
    /// <param name="viewpoint">
    /// The two rotations to draw with, when the caller has its own answer. Null takes the bounding
    /// box for the standing pose and the camera's angles for the view.
    /// </param>
    public static void Render(
        UnityMesh mesh, Camera camera, RenderTarget target, IReadOnlyList<PreviewImage?>? textures = null,
        UnityMesh? framing = null, Viewpoint? viewpoint = null)
    {
        if (target.IsEmpty) return;

        var (bgra, width, height) = (target.Bgra, target.Width, target.Height);
        Array.Clear(bgra);

        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return;

        var normals = mesh.Get(VertexAttribute.Normal);
        var uvs = mesh.Get(VertexAttribute.TexCoord0);
        var held = framing ?? mesh;
        var (centre, size, radius) = Bounds(held, held.Get(VertexAttribute.Position) ?? positions);
        var upright = viewpoint?.Standing ?? Upright.For(size).AsBasis();
        if (radius <= 0) radius = 1;

        var view = viewpoint?.View ?? View(camera);
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
        Span<float> away = stackalloc float[3];
        Span<float> u = stackalloc float[3];
        Span<float> v = stackalloc float[3];

        var shelled = mesh.CarriesAnOutline;

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
                    away[corner] = shelled ? Facing(view, upright, normals!, vertex) : 1f;
                    u[corner] = uvs is null ? 0 : uvs[vertex * 2];
                    v[corner] = uvs is null ? 0 : uvs[vertex * 2 + 1];
                }
                if (!ok) continue;

                // The outline shell, left out. Its near side is the side facing away from you, which
                // is exactly how the game draws it as a silhouette and never as a surface.
                if (away[0] + away[1] + away[2] < 0) continue;

                Fill(bgra, depth, width, height, sx, sy, sz, shade, u, v, texture);
            }
        }
    }

    /// How this model is stood up when nobody says otherwise: the bounding box sorted by extent.
    ///
    /// Exposed so something that knows better — <see cref="Facing"/> — can start from this answer
    /// and correct it rather than work out a second one.
    public static Basis Standing(UnityMesh mesh)
    {
        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return Basis.Identity;

        var (_, size, _) = Bounds(mesh, positions);
        return Upright.For(size).AsBasis();
    }

    /// Where the model lands on screen for a given view, in half-frames from the middle: -1 is the
    /// left or top edge of a square frame, +1 the right or bottom.
    ///
    /// Measured from the vertices rather than from what was drawn, because a drawing is clipped.
    /// A model three times too wide for the frame covers it edge to edge, and everything read off
    /// that says the framing is already perfect — which is why an icon of a long weapon, zoomed in,
    /// came out with both ends cut off however many times the framing was corrected.
    public static (float Left, float Top, float Right, float Bottom)? Extent(
        UnityMesh mesh, Camera camera, Viewpoint? viewpoint = null)
    {
        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return null;

        var (centre, size, radius) = Bounds(mesh, positions);
        if (radius <= 0) radius = 1;

        var upright = viewpoint?.Standing ?? Upright.For(size).AsBasis();
        var view = viewpoint?.View ?? View(camera);
        var (pivotX, pivotY, pivotZ) =
            (camera.PivotX * radius, camera.PivotY * radius, camera.PivotZ * radius);

        // The same projection the rasterizer does, with the frame's own size divided back out: a
        // half-frame is `radius * Distance` of model, whatever the target happens to be.
        var span = radius * camera.Distance;
        float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var (mx, my, mz) = upright.Apply(
                positions[v * 3] - centre.X, positions[v * 3 + 1] - centre.Y, positions[v * 3 + 2] - centre.Z);
            var (x, y, _) = view.Apply(mx - pivotX, my - pivotY, mz - pivotZ);

            var (sx, sy) = (x / span, -y / span);
            if (sx < left) left = sx;
            if (sx > right) right = sx;
            if (sy < top) top = sy;
            if (sy > bottom) bottom = sy;
        }

        return right < left ? null : (left, top, right, bottom);
    }

    /// How much a vertex's normal points at the viewer. Negative is turned away.
    private static float Facing(Basis view, Basis upright, float[] normals, int vertex)
    {
        var (ux, uy, uz) = upright.Apply(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
        var (_, _, nz) = view.Apply(ux, uy, uz);
        return nz;
    }

    /// A single light over the viewer's shoulder, with enough ambient that faces turned away stay
    /// readable instead of going black.
    private static float Lambert(Basis view, Basis upright, float[] normals, int vertex)
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

        /// The same permutation as three rows, so something that stands a model up another way can
        /// hand over a rotation and be treated identically.
        public Basis AsBasis() => new(
            Right == 0 ? 1 : 0, Right == 1 ? 1 : 0, Right == 2 ? 1 : 0,
            Up == 0 ? 1 : 0, Up == 1 ? 1 : 0, Up == 2 ? 1 : 0,
            Depth == 0 ? Flip : 0, Depth == 1 ? Flip : 0, Depth == 2 ? Flip : 0);
    }

    /// The camera's axes, as three rows that turn a model-space vector into view space.
    public readonly record struct Basis(
        float Rx, float Ry, float Rz, float Ux, float Uy, float Uz, float Fx, float Fy, float Fz)
    {
        public static Basis Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public (float X, float Y, float Z) Apply(float x, float y, float z)
            => (Rx * x + Ry * y + Rz * z, Ux * x + Uy * y + Uz * z, Fx * x + Fy * y + Fz * z);
    }

    public static Basis View(Camera camera)
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

        // Screen-right is the opposite of the axis that comes out of the frame above, because
        // forward points at the viewer: they are standing on the far side of the model from Unity's
        // own camera, and what that camera has on its right is on their left. Taking it as right
        // drew every model as its own mirror image — invisible on a gun, obvious the moment a
        // texture has writing on it.
        //
        // Flipped here, after the roll, rather than by building the frame the other way round. The
        // roll turns the two screen axes into each other, so a frame that starts out mirrored is
        // not a mirrored frame once it has been rolled — it is a different view altogether.
        return new Basis(-rx, -ry, -rz, ux, uy, uz, fx, fy, fz);
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
