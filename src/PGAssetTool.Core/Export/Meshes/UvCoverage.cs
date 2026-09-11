namespace PGAssetTool.Core.Export.Meshes;

/// Which texels of a texture a model actually samples.
///
/// A weapon's texture is an atlas: the gun, its sights, a few details, and a great deal of nothing
/// between them. Which part is which is written in the mesh's UVs and nowhere else, so an author
/// opening the image has to work it out by painting and looking — and the parts that are never
/// sampled look exactly like the parts that are.
///
/// This rasterises the triangles into the texture's own grid and answers which texels they land on,
/// so the export can clear the rest.
public static class UvCoverage
{
    /// How far a mask has to reach past the triangles for a texture the game reads this way.
    ///
    /// Point filtering with no mip chain reads exactly the texel under the coordinate and nothing
    /// beside it — and that is nearly everything here: 347 of the 367 textures in the bundle holding
    /// the weapons' own art are point-filtered with one mip. For those the mask is the UVs exactly,
    /// which is what an author sees when they open the same model in Blender, and a margin would
    /// only be the tool disagreeing with them. Anything filtered or mipped does read its neighbour,
    /// and one texel covers that.
    ///
    /// It was a flat two before this was looked at, which on a 64x64 texture nearly doubled what the
    /// mask claimed: 65% of #16's atlas against the 37% its model actually reads.
    public static int MarginFor(int filterMode, int mipCount)
        => filterMode == 0 && mipCount <= 1 ? 0 : 1;

    /// How far outside the square a coordinate may sit before it counts as leaving it.
    private const float Slack = 0.001f;

    /// The texels these submeshes sample, or null when the answer is "all of it".
    ///
    /// Null rather than a mask of everything, so a caller can tell "keep the whole thing" from
    /// "keep this part" without comparing. It comes back for a mesh with no UVs to read and for one
    /// whose UVs leave the square, which is how a texture is tiled: every texel is then in use, and
    /// clearing any of it would be wrong.
    /// <param name="uses">Each mesh and the submesh of it drawn with this texture.</param>
    /// <param name="margin">How far to reach past the triangles; see MarginFor.</param>
    public static bool[]? Of(
        IEnumerable<(UnityMesh Mesh, int SubMesh)> uses, int width, int height, int margin)
    {
        if (width <= 0 || height <= 0) return null;

        var used = new bool[width * height];
        var any = false;

        Span<float> px = stackalloc float[3];
        Span<float> py = stackalloc float[3];

        foreach (var (mesh, part) in uses)
        {
            if (mesh.Get(VertexAttribute.TexCoord0) is not { } uv) return null;

            var wide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.TexCoord0, 2);
            if (wide < 2) return null;

            var parts = mesh.SubMeshes.Count > 0
                ? mesh.SubMeshes
                : [new SubMesh(0, mesh.Indices.Length, 0, 0)];
            if (part < 0 || part >= parts.Count) continue;

            var from = Math.Max(parts[part].IndexStart, 0);
            var to = Math.Min(from + parts[part].IndexCount, mesh.Indices.Length);


            for (var i = from; i + 2 < to; i += 3)
            {
                var ok = true;

                for (var corner = 0; corner < 3; corner++)
                {
                    var vertex = mesh.Indices[i + corner];
                    if (vertex < 0 || (long)vertex * wide + 1 >= uv.Length) { ok = false; break; }

                    var u = uv[vertex * wide];
                    var v = uv[vertex * wide + 1];
                    if (u < -Slack || u > 1 + Slack || v < -Slack || v > 1 + Slack) return null;

                    px[corner] = u * width;

                    // Unity's V runs up from the bottom; a mask laid over a PNG runs down from the top.
                    py[corner] = (1 - v) * height;
                }

                if (!ok) continue;

                Fill(used, width, height, px, py);
                any = true;
            }
        }

        return any ? Grow(used, width, height, margin) : null;
    }

    private static void Fill(bool[] used, int width, int height, Span<float> px, Span<float> py)
    {
        // The corners themselves, always. A triangle smaller than a texel covers no sample point
        // and would otherwise mark nothing at all — and the small ones are the details.
        for (var corner = 0; corner < 3; corner++) Mark(used, width, height, px[corner], py[corner]);

        var left = (int)MathF.Floor(MathF.Min(px[0], MathF.Min(px[1], px[2])));
        var right = (int)MathF.Ceiling(MathF.Max(px[0], MathF.Max(px[1], px[2])));
        var top = (int)MathF.Floor(MathF.Min(py[0], MathF.Min(py[1], py[2])));
        var bottom = (int)MathF.Ceiling(MathF.Max(py[0], MathF.Max(py[1], py[2])));

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Min(right, width - 1);
        bottom = Math.Min(bottom, height - 1);

        var area = Edge(px[0], py[0], px[1], py[1], px[2], py[2]);

        // Three points on a line enclose nothing, and the corners above have it covered.
        if (MathF.Abs(area) < 1e-6f) return;

        for (var y = top; y <= bottom; y++)
        for (var x = left; x <= right; x++)
        {
            float sx = x + 0.5f, sy = y + 0.5f;

            // Divided by the area so all three come out positive inside whichever way the triangle
            // is wound, and a hair of slack so a sample exactly on an edge counts as in.
            var a = Edge(px[1], py[1], px[2], py[2], sx, sy) / area;
            var b = Edge(px[2], py[2], px[0], py[0], sx, sy) / area;
            var c = Edge(px[0], py[0], px[1], py[1], sx, sy) / area;

            if (a >= -0.0001f && b >= -0.0001f && c >= -0.0001f) used[y * width + x] = true;
        }
    }

    private static void Mark(bool[] used, int width, int height, float x, float y)
    {
        var px = Math.Clamp((int)MathF.Floor(x), 0, width - 1);
        var py = Math.Clamp((int)MathF.Floor(y), 0, height - 1);
        used[py * width + px] = true;
    }

    private static float Edge(float ax, float ay, float bx, float by, float cx, float cy)
        => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    /// Spreads the mask outwards by the margin, in two passes rather than one square per texel.
    private static bool[] Grow(bool[] used, int width, int height, int margin)
    {
        var across = new bool[used.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!used[y * width + x]) continue;
            for (var at = Math.Max(x - margin, 0); at <= Math.Min(x + margin, width - 1); at++)
                across[y * width + at] = true;
        }

        var grown = new bool[used.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!across[y * width + x]) continue;
            for (var at = Math.Max(y - margin, 0); at <= Math.Min(y + margin, height - 1); at++)
                grown[at * width + x] = true;
        }

        return grown;
    }
}
