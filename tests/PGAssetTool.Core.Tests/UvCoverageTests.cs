using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Tests;

/// What an exported texture keeps, worked out from the UVs of the model that reads it.
///
/// Pure arithmetic over coordinates, which is why it is testable without the game — and it is the
/// arithmetic that decides whether an author's picture comes out matching the UV layout they see in
/// Blender or a texel wider on every island.
public class UvCoverageTests
{
    private const int Size = 64;

    /// A quad over the given corner of the texture, in Unity's coordinates: v runs up from the
    /// bottom, so the mask's own rows come out the other way round.
    private static UnityMesh Quad(float u0, float v0, float u1, float v1) => Mesh(
        [u0, v0, u1, v0, u1, v1, u0, v1],
        [0, 1, 2, 0, 2, 3]);

    private static UnityMesh Mesh(float[] uv, int[] indices) => new()
    {
        Name = "coverage",
        VertexCount = uv.Length / 2,
        Attributes = new Dictionary<VertexAttribute, float[]> { [VertexAttribute.TexCoord0] = uv },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.TexCoord0] = 2 },
        Indices = indices,
        SubMeshes = [new SubMesh(0, indices.Length, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    private static bool[] Of(UnityMesh mesh, int margin = 0)
        => UvCoverage.Of([(mesh, 0)], Size, Size, margin)
           ?? throw new InvalidOperationException("the whole texture came back as used");

    private static bool At(bool[] used, int x, int y) => used[y * Size + x];

    [Fact]
    public void AnIslandKeepsTheTexelsItCoversAndNoMore()
    {
        // Exactly the first quarter of the texture, which on a 64-wide one is texels 0 to 15.
        var used = Of(Quad(0, 0.75f, 0.25f, 1f));

        Assert.Equal(16 * 16, used.Count(u => u));
        Assert.True(At(used, 0, 0));
        Assert.True(At(used, 15, 15));
    }

    /// The one an author notices: a coordinate landing exactly on a texel boundary belongs, as far
    /// as arithmetic goes, to the texel on the far side of it. Pixel-art UVs land on boundaries
    /// constantly, so taking every texel a triangle touches grows every island by a row and a
    /// column that the model never shows.
    [Fact]
    public void AnIslandEndingOnATexelBoundaryDoesNotClaimTheTexelBeyondIt()
    {
        var used = Of(Quad(0, 0.75f, 0.25f, 1f));

        Assert.False(At(used, 16, 0));
        Assert.False(At(used, 0, 16));
        Assert.False(At(used, 16, 16));
    }

    [Fact]
    public void HalfATexelIsEnoughToCountAsRead()
    {
        // Ends half way through texel 16, which the game reads for half of that island's width.
        var used = Of(Quad(0, 0.75f, 0.2578125f, 1f));

        Assert.True(At(used, 16, 0));
        Assert.False(At(used, 17, 0));
    }

    /// Unity's v runs up from the bottom and an image's rows run down from the top, so a mask that
    /// did not turn one into the other would keep the mirror image of the right region — and rub out
    /// the part of the weapon the model actually shows.
    [Fact]
    public void TheMaskIsTheRightWayUp()
    {
        var used = Of(Quad(0, 0, 0.25f, 0.25f));

        Assert.True(At(used, 0, Size - 1));
        Assert.False(At(used, 0, 0));
    }

    /// A face painted from a single point of the atlas has no area in UV space at all, and the game
    /// still reads the texel that point falls in.
    [Fact]
    public void AFaceWithNoAreaKeepsTheTexelItReads()
    {
        var used = Of(Mesh([0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f], [0, 1, 2]));

        Assert.Equal(1, used.Count(u => u));
        Assert.True(At(used, 32, 32));
    }

    [Fact]
    public void AMarginReachesPastTheIslandOnEverySide()
    {
        var used = Of(Quad(0.25f, 0.25f, 0.5f, 0.5f), margin: 1);

        Assert.Equal(18 * 18, used.Count(u => u));
    }

    /// UVs outside the square are how a texture is tiled, and then every texel of it is in use.
    [Fact]
    public void ATiledTextureIsUsedWhole()
    {
        Assert.Null(UvCoverage.Of([(Quad(0, 0, 2f, 2f), 0)], Size, Size, 0));
    }

    [Fact]
    public void AMeshWithNoCoordinatesSaysNothingAboutTheTexture()
    {
        var mesh = new UnityMesh
        {
            Name = "bare",
            VertexCount = 3,
            Attributes = new Dictionary<VertexAttribute, float[]>(),
            Dimensions = new Dictionary<VertexAttribute, int>(),
            Indices = [0, 1, 2],
            SubMeshes = [new SubMesh(0, 3, 0, 0)],
            BindPoses = [],
            BoneNameHashes = [],
        };

        Assert.Null(UvCoverage.Of([(mesh, 0)], Size, Size, 0));
    }

    /// Point filtering with no mip chain reads exactly the texel under the coordinate, which is
    /// nearly every texture in this game; anything filtered or mipped reads its neighbour too.
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 8, 1)]
    [InlineData(1, 1, 1)]
    public void TheMarginFollowsHowTheGameReadsTheTexture(int filter, int mips, int expected)
        => Assert.Equal(expected, UvCoverage.MarginFor(filter, mips));
}
