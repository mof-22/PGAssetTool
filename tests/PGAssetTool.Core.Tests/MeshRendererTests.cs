using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

public class MeshRendererTests
{
    private const int Size = 64;

    private static UnityMesh Quad(float z = 0, float scale = 1) => Mesh(
        [-scale, -scale, z, scale, -scale, z, scale, scale, z, -scale, scale, z],
        [0, 1, 2, 0, 2, 3]);

    private static UnityMesh Mesh(float[] positions, int[] indices) => new()
    {
        Name = "preview",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]> { [VertexAttribute.Position] = positions },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
        Indices = indices,
        SubMeshes = [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    private static byte[] Draw(UnityMesh mesh, Camera camera)
    {
        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(mesh, camera, target);
        return target.Bgra;
    }

    private static int Covered(byte[] pixels)
    {
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) count++;
        return count;
    }

    /// The colour written at a pixel, or null where nothing was drawn.
    private static byte? At(byte[] pixels, int x, int y)
    {
        var at = (y * Size + x) * 4;
        return pixels[at + 3] == 0 ? null : pixels[at];
    }

    [Fact]
    public void AQuadFacingTheCameraIsDrawnAroundTheMiddle()
    {
        // Looked at straight on, so the whole thing is in frame and the centre is covered.
        var pixels = Draw(Quad(), new Camera(Yaw: 0, Pitch: 0));

        Assert.True(Covered(pixels) > 0, "nothing was drawn at all");
        Assert.NotNull(At(pixels, Size / 2, Size / 2));
        Assert.Null(At(pixels, 0, 0));
    }

    [Fact]
    public void TheModelIsCentredWhateverItsCoordinatesAre()
    {
        // A mesh built a long way from the origin still fills the frame: the camera looks at the
        // model's own centre, not at zero.
        var far = Mesh([99, 99, 0, 101, 99, 0, 101, 101, 0, 99, 101, 0], [0, 1, 2, 0, 2, 3]);

        Assert.NotNull(At(Draw(far, new Camera(Yaw: 0, Pitch: 0)), Size / 2, Size / 2));
    }

    [Fact]
    public void ALargeModelAndASmallOneFillTheFrameTheSame()
    {
        // The distance is a multiple of the model's radius, so a pistol and a rocket launcher both
        // arrive framed rather than one being a speck.
        var straight = new Camera(Yaw: 0, Pitch: 0);
        var small = Covered(Draw(Quad(scale: 0.01f), straight));
        var large = Covered(Draw(Quad(scale: 100f), straight));

        Assert.Equal(small, large);
    }

    [Fact]
    public void TheNearerSurfaceIsTheOneYouSee()
    {
        // Two quads at the same place on screen, one in front of the other, shaded differently.
        // Both meshes carry all eight vertices so the framing is identical and only the triangle
        // list differs — otherwise the larger bounding box would change the scale and hide the
        // thing being measured.
        float[] positions =
        [
            -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1,        // near, facing the camera
            -1, -1, -1, 1, -1, -1, 1, 1, -1, -1, 1, -1,    // far, facing away and down
        ];
        float[] normals =
        [
            0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1,
            0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0,
        ];

        int[] near = [0, 1, 2, 0, 2, 3];
        int[] far = [4, 5, 6, 4, 6, 7];

        var camera = new Camera(Yaw: 0, Pitch: 0);
        var expected = At(Draw(Lit(positions, normals, near), camera), Size / 2, Size / 2);
        Assert.NotNull(expected);

        // Whichever order the triangles arrive in, the near one is the one that shows.
        Assert.Equal(expected, At(Draw(Lit(positions, normals, [.. near, .. far]), camera), Size / 2, Size / 2));
        Assert.Equal(expected, At(Draw(Lit(positions, normals, [.. far, .. near]), camera), Size / 2, Size / 2));

        // And the far quad really would have looked different, or the check above proves nothing.
        Assert.NotEqual(expected, At(Draw(Lit(positions, normals, far), camera), Size / 2, Size / 2));
    }

    private static UnityMesh Lit(float[] positions, float[] normals, int[] indices) => new()
    {
        Name = "preview",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = positions,
            [VertexAttribute.Normal] = normals,
        },
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.Normal] = 3,
        },
        Indices = indices,
        SubMeshes = [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    [Fact]
    public void TurningAllTheWayRoundComesBackToWhereItStarted()
    {
        var mesh = Quad();
        var start = new Camera(Yaw: 0.4f, Pitch: 0.2f);
        var round = start.Turned(MathF.Tau, 0);

        Assert.Equal(Covered(Draw(mesh, start)), Covered(Draw(mesh, round)));
    }

    [Fact]
    public void TheCameraStopsShortOfLookingStraightDown()
    {
        // At the pole the view direction and the up vector are parallel and the frame collapses.
        var camera = new Camera().Turned(0, 100f);

        Assert.True(camera.Pitch < MathF.PI / 2, $"pitch reached {camera.Pitch}");
        Assert.True(Covered(Draw(Quad(), camera)) >= 0);
    }

    [Fact]
    public void ZoomingIsBoundedAtBothEnds()
    {
        Assert.Equal(20f, new Camera().Zoomed(1000f).Distance);
        Assert.Equal(0.4f, new Camera().Zoomed(0.0001f).Distance);
    }

    [Fact]
    public void AMeshWithNothingInItDrawsNothingRatherThanThrowing()
    {
        var empty = Mesh([], []);
        Assert.Equal(0, Covered(Draw(empty, new Camera())));
    }

    [Fact]
    public void IndicesPastTheEndOfTheVertexListAreIgnored()
    {
        // A mesh read from a bundle is not guaranteed well formed, and a preview must not be the
        // thing that crashes on one.
        var broken = Mesh([0, 0, 0, 1, 0, 0, 1, 1, 0], [0, 1, 99]);
        Assert.Equal(0, Covered(Draw(broken, new Camera())));
    }

    /// A long thin bar along one axis, as a stand-in for a barrel.
    private static UnityMesh Bar(int axis, float length = 2f, float thickness = 0.2f)
    {
        float[] size = [thickness, thickness, thickness];
        size[axis] = length;

        var positions = new List<float>();
        foreach (var corner in new[] { -1f, 1f })
        foreach (var side in new[] { -1f, 1f })
        {
            // Two triangles per end, enough to give the bar an extent on every axis.
            var other = (axis + 1) % 3;
            var third = (axis + 2) % 3;
            float[] v = new float[3];
            v[axis] = corner * size[axis] / 2;
            v[other] = side * size[other] / 2;
            v[third] = 0;
            positions.AddRange(v);
        }
        return Mesh([.. positions], [0, 1, 2, 1, 2, 3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ALongModelLiesAcrossTheScreenWhicheverAxisItWasBuiltOn(int axis)
    {
        // A Mesh carries no orientation of its own: in the game the renderer's transform places it,
        // and a preview has nothing to place it with. Weapons are authored with the barrel along Y,
        // so without this they hang straight down.
        var pixels = Draw(Bar(axis), new Camera(Yaw: 0, Pitch: 0));

        var (minX, maxX, minY, maxY) = Extent(pixels);
        Assert.True(maxX - minX > maxY - minY,
            $"a bar along axis {axis} drew {maxX - minX} wide by {maxY - minY} tall");
    }

    [Fact]
    public void TheCorrectionDoesNotMirrorTheModel()
    {
        // Reordering axes can flip handedness. A bar with its mass to one side has to stay on that
        // side, or every preview of an asymmetric model would be a mirror image of the real thing.
        float[] positions = [0, 0, 0, 2, 0, 0, 2, 0.4f, 0, 0, 0.1f, 0];
        var lopsided = Mesh(positions, [0, 1, 2, 0, 2, 3]);

        var pixels = Draw(lopsided, new Camera(Yaw: 0, Pitch: 0));

        // The tall end is at +X in the model, so more of the drawing sits right of centre than left.
        var (left, right) = (0, 0);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (At(pixels, x, y) is not null)
            {
                if (x < Size / 2) left++; else right++;
            }

        Assert.True(right > left, $"the heavy end drew {right} pixels right and {left} left");
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) Extent(byte[] pixels)
    {
        int minX = Size, maxX = -1, minY = Size, maxY = -1;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (At(pixels, x, y) is not null)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return (minX, maxX, minY, maxY);
    }

    [Fact]
    public void RedrawingTheSameSizeAllocatesNothing()
    {
        // Allocating a depth buffer per frame is what made a large preview pane expensive — eleven
        // megabytes a frame at 2000x1500, all of it immediately garbage.
        var mesh = Quad();
        var target = new RenderTarget();
        target.Resize(Size, Size);
        var angles = Enumerable.Range(0, 20).Select(i => new Camera(Yaw: i * 0.1f)).ToArray();
        MeshRenderer.Render(mesh, angles[0], target);   // warm anything lazy

        // Per-thread, not process-wide: xUnit runs test classes in parallel, so the process-wide
        // counter would pick up whatever another test happened to be doing.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 20; frame++)
            MeshRenderer.Render(mesh, angles[frame], target);

        // A Camera is a record, so allocating one per frame would be the test's own doing, not the
        // renderer's; the angles above are made in advance so only drawing is measured.
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ResizingKeepsTheBuffersInStepWithEachOther()
    {
        var target = new RenderTarget();
        target.Resize(320, 200);
        Assert.Equal(320 * 200 * 4, target.Bgra.Length);

        target.Resize(64, 64);
        Assert.Equal(64 * 64 * 4, target.Bgra.Length);

        // A stale depth buffer from the larger size would leave the new frame testing against
        // whatever the old one held.
        MeshRenderer.Render(Quad(), new Camera(Yaw: 0, Pitch: 0), target);
        Assert.True(Covered(target.Bgra) > 0);
    }

    [Fact]
    public void ATargetWithNoSizeYetDrawsNothingRatherThanThrowing()
        => MeshRenderer.Render(Quad(), new Camera(), new RenderTarget());

    /// A solid image of one colour, to see where it lands.
    private static PreviewImage Swatch(byte blue, byte green, byte red, int size = 4)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (blue, green, red, (byte)255);
        return new PreviewImage(size, size, pixels);
    }

    private static UnityMesh Textured(float[] positions, float[] uvs, int[] indices, params SubMesh[] parts) => new()
    {
        Name = "textured",
        VertexCount = positions.Length / 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = positions,
            [VertexAttribute.TexCoord0] = uvs,
        },
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.TexCoord0] = 2,
        },
        Indices = indices,
        SubMeshes = parts.Length > 0 ? parts : [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    private static UnityMesh TexturedQuad(params SubMesh[] parts) => Textured(
        [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
        [0, 0, 1, 0, 1, 1, 0, 1],
        [0, 1, 2, 0, 2, 3],
        parts);

    [Fact]
    public void AMeshWithNoTextureIsStillDrawn()
    {
        // Nothing resolves a texture for a great many meshes, and those must not come out blank.
        Assert.True(Covered(Draw(TexturedQuad(), new Camera(Yaw: 0, Pitch: 0))) > 0);
    }

    [Fact]
    public void TheTextureIsWhatShowsRatherThanTheFlatShade()
    {
        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(TexturedQuad(), new Camera(Yaw: 0, Pitch: 0), target, [Swatch(200, 0, 0)]);

        var at = ((Size / 2) * Size + Size / 2) * 4;
        Assert.True(target.Bgra[at] > target.Bgra[at + 2],
            $"expected the blue swatch, got B={target.Bgra[at]} G={target.Bgra[at + 1]} R={target.Bgra[at + 2]}");
    }

    [Fact]
    public void EachSubmeshTakesTheTextureOfItsOwnMaterial()
    {
        // Unity pairs submesh i with material i. Getting this wrong shows part of a weapon in
        // another part's colours, which is hard to trace back to the preview.
        var twoHalves = Textured(
            [-1, -1, 0, 0, -1, 0, 0, 1, 0, -1, 1, 0,      // left half
             0, -1, 0, 1, -1, 0, 1, 1, 0, 0, 1, 0],       // right half
            [0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7],
            new SubMesh(0, 6, 0, 0), new SubMesh(6, 6, 0, 0));

        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(twoHalves, new Camera(Yaw: 0, Pitch: 0), target,
            [Swatch(200, 0, 0), Swatch(0, 0, 200)]);

        // Well inside the drawing: the model is framed with a margin, so a quarter of the way in
        // from the edge falls outside it.
        var left = (Size / 2 * Size + Size / 2 - 8) * 4;
        var right = (Size / 2 * Size + Size / 2 + 8) * 4;

        Assert.True(target.Bgra[left] > target.Bgra[left + 2], "the left half did not take the first texture");
        Assert.True(target.Bgra[right + 2] > target.Bgra[right], "the right half did not take the second texture");
    }

    [Fact]
    public void FewerTexturesThanSubmeshesLeavesTheRestPlain()
    {
        // A material that resolves to nothing must not silently borrow the previous one.
        var twoParts = TexturedQuad(new SubMesh(0, 3, 0, 0), new SubMesh(3, 3, 0, 0));

        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(twoParts, new Camera(Yaw: 0, Pitch: 0), target, [Swatch(200, 0, 0), null]);

        Assert.True(Covered(target.Bgra) > 0);
    }

    [Fact]
    public void CoordinatesOutsideTheUnitSquareWrapRatherThanClamp()
    {
        // A mesh is free to tile its texture, and clamping would smear the edge texel across it.
        var tiled = Textured(
            [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
            [0, 0, 3, 0, 3, 3, 0, 3],
            [0, 1, 2, 0, 2, 3]);

        var target = new RenderTarget();
        target.Resize(Size, Size);
        MeshRenderer.Render(tiled, new Camera(Yaw: 0, Pitch: 0), target, [Swatch(200, 0, 0)]);

        Assert.True(Covered(target.Bgra) > 0);
    }

    [Fact]
    public void RollTiltsTheModelInThePlaneOfTheScreen()
    {
        // A bar lying across the screen, rolled a quarter turn, has to end up standing up it —
        // and nothing about which side faces the viewer may change, which is what separates this
        // from turning the camera.
        var bar = Mesh([-1, -0.1f, 0, 1, -0.1f, 0, 1, 0.1f, 0, -1, 0.1f, 0], [0, 1, 2, 0, 2, 3]);

        var flat = Draw(bar, new Camera(Yaw: 0, Pitch: 0));
        var tilted = Draw(bar, new Camera(Yaw: 0, Pitch: 0).Rolled(MathF.PI / 2));

        Assert.NotNull(At(flat, Size / 2, Size / 2));
        Assert.NotNull(At(tilted, Size / 2, Size / 2));

        // Wide before, tall after: measured rather than assumed, because a roll applied to the
        // wrong pair of axes still draws something and still looks plausible in one frame.
        Assert.True(Width(flat) > Height(flat), "the bar was not drawn lying down to begin with");
        Assert.True(Height(tilted) > Width(tilted), "rolling it a quarter turn did not stand it up");
    }

    [Fact]
    public void AFullTurnOfRollComesBackToWhereItStarted()
    {
        var bar = Mesh([-1, -0.1f, 0, 1, -0.1f, 0, 1, 0.1f, 0, -1, 0.1f, 0], [0, 1, 2, 0, 2, 3]);

        Assert.Equal(
            Covered(Draw(bar, new Camera(Yaw: 0.4f, Pitch: 0.2f))),
            Covered(Draw(bar, new Camera(Yaw: 0.4f, Pitch: 0.2f).Rolled(MathF.Tau))),
            tolerance: 4);
    }

    private static int Width(byte[] pixels) => Extent(pixels, horizontal: true);
    private static int Height(byte[] pixels) => Extent(pixels, horizontal: false);

    private static int Extent(byte[] pixels, bool horizontal)
    {
        int low = Size, high = -1;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(pixels, x, y) is null) continue;
                var along = horizontal ? x : y;
                low = Math.Min(low, along);
                high = Math.Max(high, along);
            }
        return high < low ? 0 : high - low + 1;
    }

    [Fact]
    public void PanningMovesWhereTheModelSitsWithoutTurningIt()
    {
        // Half a frame to the right means half a frame to the right, and the model itself is
        // unchanged — which is what separates a pan from an orbit that happens to look similar.
        var quad = Quad(scale: 0.3f);

        var centred = Draw(quad, new Camera(Yaw: 0, Pitch: 0));
        var moved = Draw(quad, new Camera(Yaw: 0, Pitch: 0).Panned(0.5f, 0));

        Assert.Equal(Covered(centred), Covered(moved), tolerance: 4);
        Assert.Equal(Middle(centred) + Size / 4, Middle(moved), tolerance: 2);
    }

    [Fact]
    public void PanningUpMovesItUpTheScreenRatherThanDownIt()
    {
        var quad = Quad(scale: 0.3f);

        var centred = Rows(Draw(quad, new Camera(Yaw: 0, Pitch: 0)));
        var raised = Rows(Draw(quad, new Camera(Yaw: 0, Pitch: 0).Panned(0, 0.5f)));

        Assert.True(raised < centred, $"panning up put it at row {raised}, below row {centred}");
    }

    /// The middle column of whatever was drawn.
    private static int Middle(byte[] pixels)
    {
        int low = Size, high = -1;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(pixels, x, y) is null) continue;
                low = Math.Min(low, x);
                high = Math.Max(high, x);
            }
        return (low + high) / 2;
    }

    /// The middle row of whatever was drawn.
    private static int Rows(byte[] pixels)
    {
        int low = Size, high = -1;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (At(pixels, x, y) is null) continue;
                low = Math.Min(low, y);
                high = Math.Max(high, y);
            }
        return (low + high) / 2;
    }
}
