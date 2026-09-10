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

    /// A box. Its normals point straight out from the middle, and its triangles are wound so that
    /// the two agree — which is what a solid looks like.
    ///
    /// <param name="inside">
    /// Turn it inside out: the normals point in and the triangles are wound the other way, so the
    /// two still agree with each other and disagree with the world. That is a shell.
    /// </param>
    private static (float[] Positions, float[] Normals, int[] Indices) Box(float size, bool inside = false)
    {
        float[] corners =
        [
            -size, -size, -size,  size, -size, -size,  size, size, -size,  -size, size, -size,
            -size, -size,  size,  size, -size,  size,  size, size,  size,  -size, size,  size,
        ];

        var normals = new float[corners.Length];
        for (var i = 0; i < corners.Length; i++)
            normals[i] = (inside ? -corners[i] : corners[i]) / (size * MathF.Sqrt(3));

        // Each face wound counter-clockwise seen from outside, so (b-a) x (c-a) points outwards.
        int[] faces =
        [
            4, 5, 6, 4, 6, 7,   0, 3, 2, 0, 2, 1,   1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3,   3, 7, 6, 3, 6, 2,   0, 1, 5, 0, 5, 4,
        ];

        if (inside)
            for (var i = 0; i + 2 < faces.Length; i += 3)
                (faces[i + 1], faces[i + 2]) = (faces[i + 2], faces[i + 1]);

        return (corners, normals, faces);
    }

    /// A solid whose normals were turned round and whose triangles were not, so the two disagree
    /// and neither can be trusted. #14 Battle Shovel's head is built this way.
    private static (float[] Positions, float[] Normals, int[] Indices) Muddled(float size)
    {
        var box = Box(size);
        return (box.Positions, [.. box.Normals.Select(n => -n)], box.Indices);
    }

    private static UnityMesh Solid((float[] Positions, float[] Normals, int[] Indices) box)
        => Solid(box.Positions, box.Normals, box.Indices);

    private static UnityMesh Solid(float[] positions, float[] normals, int[] indices) => new()
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

    /// Two boxes in one mesh: the second is the first, larger and turned inside out.
    private static UnityMesh WithAnOutline()
    {
        var body = Box(1f);
        var shell = Box(1.15f, inside: true);

        var offset = body.Positions.Length / 3;
        return Solid(
            [.. body.Positions, .. shell.Positions],
            [.. body.Normals, .. shell.Normals],
            [.. body.Indices, .. shell.Indices.Select(i => i + offset)]);
    }

    [Fact]
    public void AnOutlineShellIsRecognisedAndAPlainSolidIsNot()
    {
        // The game draws a silhouette by wrapping the model in a copy of itself, larger and turned
        // inside out, and showing only the side of it that faces away. Drawn as an ordinary
        // surface it covers the model completely — Punk's Shovel came up a flat magenta blob.
        Assert.True(WithAnOutline().CarriesAnOutline);
        Assert.False(Solid(Box(1f)).CarriesAnOutline);
    }

    [Fact]
    public void AMeshWhoseNormalsFightItsWindingIsLeftAlone()
    {
        // #14 Battle Shovel: its head is wound one way and shaded the other, so the normals say
        // nothing about which side is out. It leans inwards like a shell and is not one, and
        // culling it by facing hollows the head — so the two tests are asked together.
        Assert.False(Solid(Muddled(1f)).CarriesAnOutline);
    }

    [Fact]
    public void AMeshWithNoNormalsCarriesNoOutline()
        => Assert.False(Quad().CarriesAnOutline);

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
    public void TheCameraStopsAtTheTopRatherThanTumblingOverIt()
    {
        // Straight up and straight down are as far as it goes, and it arrives exactly there rather
        // than at a wall short of it — the frame is square at the pole, so there is nothing to
        // stand back from.
        Assert.Equal(MathF.PI / 2, new Camera().Turned(0, 9f).Pitch);
        Assert.Equal(-MathF.PI / 2, new Camera().Turned(0, -9f).Pitch);
        Assert.Equal(MathF.PI / 2, new Camera().Turned(0, MathF.Tau).Pitch);

        // A solid rather than a flat one: a plane seen exactly edge-on draws nothing however good
        // the frame is, which says nothing about the frame.
        var solid = Mesh(
            [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1],
            [0, 1, 2, 0, 1, 3, 0, 2, 3, 1, 2, 3]);

        foreach (var pitch in new[] { MathF.PI / 2, -MathF.PI / 2 })
            Assert.True(Covered(Draw(solid, new Camera(Yaw: 0.3f, Pitch: pitch))) > 0,
                $"nothing was drawn at a pitch of {pitch}");
    }

    [Fact]
    public void DraggingRightTurnsTheModelRightAtEveryReachableAngle()
    {
        // The reason the pitch stops. Past the top the frame's up vector is inverted, and yaw —
        // which is measured about the world's up, not the frame's — then reads backwards: dragging
        // right walked the viewer left. Nothing reachable by dragging is on that side of the pole
        // any more, and this walks the whole range to say so.
        // Read through panning, which is the public way to ask which way the frame's up points:
        // dragging the model up moves the pivot down along that axis, so an inverted frame sends
        // the pivot the other way.
        for (var pitch = -MathF.PI / 2; pitch <= MathF.PI / 2; pitch += 0.15f)
        {
            var lifted = new Camera(Yaw: 0.3f, Pitch: pitch).Panned(0, 1);

            Assert.True(lifted.PivotY <= 1e-5f,
                $"the frame is upside down at a pitch of {pitch}: lifting moved the pivot to {lifted.PivotY}");
        }
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
    public void TheModelIsDrawnTheWayTheGameSeesItRatherThanMirrored()
    {
        // The ground truth is Unity's own camera: it looks along +Z and has +X on its right. So a
        // model with its mass at +X, seen from where that camera stands, draws heavy on the right,
        // and seen from the other side draws heavy on the left. A mirrored projection satisfies
        // one of those and not the other, which is the whole difficulty of noticing it: a weapon's
        // silhouette looks equally plausible either way round, and only a texture with writing on
        // it says which way the picture is facing.
        float[] positions = [0, 0, 0, 2, 0, 0, 2, 0.4f, 0, 0, 0.1f, 0];
        var lopsided = Mesh(positions, [0, 1, 2, 0, 2, 3]);

        var (left, right) = Halves(Draw(lopsided, new Camera(Yaw: MathF.PI, Pitch: 0)));
        Assert.True(right > left, $"from the game's own side the heavy end drew {right} right, {left} left");

        var (fromBehindLeft, fromBehindRight) = Halves(Draw(lopsided, new Camera(Yaw: 0, Pitch: 0)));
        Assert.True(fromBehindLeft > fromBehindRight,
            $"from behind the heavy end drew {fromBehindRight} right, {fromBehindLeft} left");
    }

    /// How much was drawn either side of the middle.
    private static (int Left, int Right) Halves(byte[] pixels)
    {
        var (left, right) = (0, 0);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            if (At(pixels, x, y) is not null)
            {
                if (x < Size / 2) left++; else right++;
            }

        return (left, right);
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

        // From the side the game's own camera looks from, where the model's -X is the screen's
        // left, so the halves are where the lines above say they are.
        MeshRenderer.Render(twoHalves, new Camera(Yaw: MathF.PI, Pitch: 0), target,
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

    [Fact]
    public void WhatWasPannedToTheMiddleStaysThereWhenTheViewTurns()
    {
        // The whole point of the change: turning happens about whatever is in the middle of the
        // frame, not about the model's own centre. Pan the right-hand end of a bar into the middle
        // and turn; it has to still be there. Turning about the model centre instead would pull it
        // away, and the middle of the frame would be empty.
        var bar = Mesh([-1, -0.2f, 0, 1, -0.2f, 0, 1, 0.2f, 0, -1, 0.2f, 0], [0, 1, 2, 0, 2, 3]);
        var straight = new Camera(Yaw: 0, Pitch: 0);

        // A half-frame is Distance radii across and the bar is about a radius long each way, so
        // this puts the middle of the frame just inside its right-hand end.
        var panned = straight.Panned(-0.6f, 0);
        Assert.NotNull(At(Draw(bar, panned), Size / 2, Size / 2));

        var turned = Draw(bar, panned.Turned(1.2f, 0));
        Assert.True(
            Enumerable.Range(Size / 2 - 2, 5).Any(y => At(turned, Size / 2, y) is not null),
            "turning moved the panned end out of the middle of the frame");
    }

    [Fact]
    public void PanningBackTheWayItCameLeavesTheViewWhereItStarted()
    {
        var quad = Quad(scale: 0.3f);
        var start = new Camera(Yaw: 0.4f, Pitch: 0.2f, Roll: 0.3f);

        var there = start.Panned(0.4f, -0.25f);
        var back = there.Panned(-0.4f, 0.25f);

        Assert.Equal(start.PivotX, back.PivotX, 5);
        Assert.Equal(start.PivotY, back.PivotY, 5);
        Assert.Equal(start.PivotZ, back.PivotZ, 5);
        Assert.Equal(Covered(Draw(quad, start)), Covered(Draw(quad, back)));
    }

    [Fact]
    public void ADragIsNothingMoreThanAYawAndAPitch()
    {
        var start = new Camera(Yaw: 0.2f, Pitch: 0.3f);

        Assert.Equal(start.Turned(0.1f, 0.2f), start.Dragged(0.1f, 0.2f));
        Assert.Equal(start.Rolled(1.1f).Turned(0.1f, 0.2f), start.Rolled(1.1f).Dragged(0.1f, 0.2f));
    }

    [Fact]
    public void ATiltDoesNotChangeWhichWayADragTurnsTheModel()
    {
        // A turntable turns about one axis and tilting your head does not change which. The drag
        // used to be taken out of the roll and split between yaw and pitch so that it followed the
        // picture; it reads well for a small drag and does not hold together, because yaw and pitch
        // do not commute — and once the pitch stopped at the poles, a sideways drag could run into
        // that stop and spend what was left of itself spinning the model about the vertical.
        var upright = new Camera(Yaw: 0.2f, Pitch: 0.3f);
        var tilted = upright.Rolled(MathF.PI / 2);

        Assert.Equal(upright.Dragged(0.25f, 0).Yaw, tilted.Dragged(0.25f, 0).Yaw, 5);
        Assert.Equal(upright.Dragged(0, 0.25f).Pitch, tilted.Dragged(0, 0.25f).Pitch, 5);

        // And each direction keeps to its own angle.
        Assert.Equal(tilted.Pitch, tilted.Dragged(0.25f, 0).Pitch, 5);
        Assert.Equal(tilted.Yaw, tilted.Dragged(0, 0.25f).Yaw, 5);
    }

    [Fact]
    public void ADragAndItsOppositeCancelAtAnyTilt()
    {
        var tilted = new Camera(Yaw: 0.2f, Pitch: 0.3f).Rolled(0.9f);

        var back = tilted.Dragged(0.2f, -0.15f).Dragged(-0.2f, 0.15f);

        Assert.Equal(tilted.Yaw, back.Yaw, 5);
        Assert.Equal(tilted.Pitch, back.Pitch, 5);
    }
}
