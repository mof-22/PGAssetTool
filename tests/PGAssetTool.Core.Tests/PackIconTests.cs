using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Core.Tests;

public class PackIconTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pgassettool-icon").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static UnityMesh Quad() => new()
    {
        Name = "quad",
        VertexCount = 4,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
        },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
        Indices = [0, 1, 2, 0, 2, 3],
        SubMeshes = [new SubMesh(0, 6, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    [Fact]
    public void TheDrawingIsSquareAndHasTheModelInIt()
    {
        var picture = PackIcon.Render(Quad(), null, size: 64);

        Assert.Equal(64, picture.Width);
        Assert.Equal(64, picture.Height);
        Assert.False(PackIcon.IsBlank(picture));
    }

    [Fact]
    public void AModelOutOfFrameIsRecognisedAsNoPictureAtAll()
    {
        // A file that is transparent from corner to corner looks exactly like a missing icon, so
        // writing one is worse than not writing anything.
        var picture = PackIcon.Render(Quad(), null, new Camera(Distance: 0.4f).Panned(40, 0), size: 64);

        Assert.True(PackIcon.IsBlank(picture));
    }

    [Fact]
    public void TheAngleItIsDrawnFromIsTheOneItWasAskedFor()
    {
        var straight = PackIcon.Render(Quad(), null, new Camera(Yaw: 0, Pitch: 0), size: 64);
        var edgeOn = PackIcon.Render(Quad(), null, new Camera(Yaw: 1.4f, Pitch: 0), size: 64);

        Assert.NotEqual(Drawn(straight), Drawn(edgeOn));
    }

    [Fact]
    public void WhatIsWrittenReadsBackAsThePictureThatWasDrawn()
    {
        var path = Path.Combine(_directory, PackIcon.FileName);
        PackIcon.Write(PackIcon.Render(Quad(), null, size: 64), path);

        using var file = File.OpenRead(path);
        var read = StbImageSharp.ImageResult.FromStream(file, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Equal(64, read.Width);
        Assert.Equal(64, read.Height);

        // Transparent where nothing was drawn: an icon sits on whatever is behind it, and a black
        // square would be a worse picture than the model on its own.
        Assert.Contains(read.Data.Where((_, i) => i % 4 == 3), a => a == 0);
        Assert.Contains(read.Data.Where((_, i) => i % 4 == 3), a => a == 255);
    }

    private static int Drawn(PreviewImage picture)
    {
        var count = 0;
        for (var i = 3; i < picture.Bgra.Length; i += 4) if (picture.Bgra[i] != 0) count++;
        return count;
    }
}
