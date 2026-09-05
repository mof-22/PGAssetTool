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
        var pixels = new byte[Size * Size * 4];
        MeshRenderer.Render(mesh, camera, pixels, Size, Size);
        return pixels;
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
}
