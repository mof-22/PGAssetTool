using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// How the model is being looked at. Angles are radians; distance is a multiple of the model's own
/// radius, so a pistol and a rocket launcher both start out filling the frame.
public sealed record Camera(float Yaw = 0.7f, float Pitch = 0.35f, float Distance = 2.6f)
{
    public Camera Turned(float dYaw, float dPitch) => this with
    {
        Yaw = Yaw + dYaw,

        // Stop just short of the poles: at exactly straight up the view direction and the up vector
        // are parallel and the frame cannot be built.
        Pitch = Math.Clamp(Pitch + dPitch, -1.55f, 1.55f),
    };

    public Camera Zoomed(float factor) => this with { Distance = Math.Clamp(Distance * factor, 0.4f, 20f) };
}

/// Draws a mesh into a pixel buffer, in software.
///
/// Avalonia has no 3D of its own, and reaching for OpenGL would trade a few hundred lines for a
/// dependency on whatever driver the machine happens to have. These meshes are tiny — the largest
/// weapon in the game is a few thousand triangles — so a plain z-buffered rasterizer redraws well
/// inside a frame and cannot fail to initialise.
public static class MeshRenderer
{
    public static void Render(UnityMesh mesh, Camera camera, byte[] bgra, int width, int height)
    {
        Array.Clear(bgra);
        if (width <= 0 || height <= 0) return;

        var positions = mesh.Get(VertexAttribute.Position);
        if (positions is null || mesh.VertexCount == 0) return;

        var normals = mesh.Get(VertexAttribute.Normal);
        var (centre, radius) = Bounds(mesh, positions);
        if (radius <= 0) radius = 1;

        var view = View(camera, centre, radius);
        var scale = Math.Min(width, height) * 0.5f / (radius * camera.Distance) * 1.6f;

        var depth = new float[width * height];
        Array.Fill(depth, float.NegativeInfinity);

        Span<float> sx = stackalloc float[3];
        Span<float> sy = stackalloc float[3];
        Span<float> sz = stackalloc float[3];
        Span<float> shade = stackalloc float[3];

        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var ok = true;
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = mesh.Indices[i + corner];
                if (vertex < 0 || vertex >= mesh.VertexCount) { ok = false; break; }

                var (x, y, z) = view.Apply(
                    positions[vertex * 3] - centre.X,
                    positions[vertex * 3 + 1] - centre.Y,
                    positions[vertex * 3 + 2] - centre.Z);

                sx[corner] = width * 0.5f + x * scale;
                sy[corner] = height * 0.5f - y * scale;
                sz[corner] = z;

                shade[corner] = normals is null ? 1f : Lambert(view, normals, vertex);
            }
            if (!ok) continue;

            Fill(bgra, depth, width, height, sx, sy, sz, shade);
        }
    }

    /// A single light over the viewer's shoulder, with enough ambient that faces turned away stay
    /// readable instead of going black.
    private static float Lambert(Basis view, float[] normals, int vertex)
    {
        var (nx, ny, nz) = view.Apply(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
        var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length <= 0) return 1f;

        // The light sits up and to the left of the camera, in view space.
        var lambert = (nx * -0.35f + ny * 0.5f + nz * 0.79f) / length;
        return 0.25f + 0.75f * Math.Max(lambert, 0f);
    }

    private static void Fill(
        byte[] bgra, float[] depth, int width, int height,
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, ReadOnlySpan<float> sz, ReadOnlySpan<float> shade)
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
            var value = (byte)(40 + 200 * lit);

            bgra[at * 4] = value;
            bgra[at * 4 + 1] = value;
            bgra[at * 4 + 2] = value;
            bgra[at * 4 + 3] = 255;
        }
    }

    /// The camera's axes, as three rows that turn a model-space vector into view space.
    private readonly record struct Basis(
        float Rx, float Ry, float Rz, float Ux, float Uy, float Uz, float Fx, float Fy, float Fz)
    {
        public (float X, float Y, float Z) Apply(float x, float y, float z)
            => (Rx * x + Ry * y + Rz * z, Ux * x + Uy * y + Uz * z, Fx * x + Fy * y + Fz * z);
    }

    private static Basis View(Camera camera, (float X, float Y, float Z) _, float __)
    {
        var (cy, sy) = (MathF.Cos(camera.Yaw), MathF.Sin(camera.Yaw));
        var (cp, sp) = (MathF.Cos(camera.Pitch), MathF.Sin(camera.Pitch));

        // Forward points from the model towards the camera, so a larger Z is *nearer* the viewer and
        // the depth test keeps the largest.
        var (fx, fy, fz) = (cp * sy, sp, cp * cy);
        var (rx, ry, rz) = (cy, 0f, -sy);
        var (ux, uy, uz) = (fy * rz - fz * ry, fz * rx - fx * rz, fx * ry - fy * rx);

        return new Basis(rx, ry, rz, ux, uy, uz, fx, fy, fz);
    }

    private static ((float X, float Y, float Z) Centre, float Radius) Bounds(UnityMesh mesh, float[] positions)
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
        var radius = MathF.Sqrt(
            MathF.Pow(maxX - centre.Item1, 2) + MathF.Pow(maxY - centre.Item2, 2) + MathF.Pow(maxZ - centre.Item3, 2));
        return (centre, radius);
    }
}
