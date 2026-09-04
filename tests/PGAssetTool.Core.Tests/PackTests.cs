using System.IO.Compression;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class PackTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("pgassettool-pack").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private PackManifest WriteWorkspace(params string[] fileNames)
    {
        var operations = new List<PackOperation>();
        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(_workspace, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"original {fileName}");
            operations.Add(new PackOperation
            {
                Op = PackOperations.ReplaceTexture,
                Target = new AssetAddress("woi_0", "Texture2D", Path.GetFileNameWithoutExtension(fileName), 0, 42),
                Source = fileName,
                BaselineSha256 = Workspace.HashFile(path),
            });
        }
        var manifest = new PackManifest { Id = "test", Name = "Test", Operations = operations };
        File.WriteAllText(Path.Combine(_workspace, PackManifest.FileName), manifest.ToJson());
        return manifest;
    }

    private void Edit(string fileName) =>
        File.AppendAllText(Path.Combine(_workspace, fileName), "edited");

    [Fact]
    public void AnUneditedWorkspaceHasNothingToPack()
    {
        WriteWorkspace("a.png", "b.png");
        Assert.Empty(Workspace.Changed(_workspace, Workspace.Read(_workspace)));
        Assert.Throws<InvalidOperationException>(
            () => PackBuilder.Build(_workspace, Path.Combine(_workspace, "out.pgmod")));
    }

    [Fact]
    public void OnlyEditedFilesBecomeOperations()
    {
        WriteWorkspace("icon/a.png", "textures/b.png", "textures/c.png");
        Edit("textures/b.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        var result = PackBuilder.Build(_workspace, output);

        Assert.Equal(1, result.Operations);
        Assert.Equal(2, result.Unchanged.Count);
        Assert.Equal("textures/b.png", Assert.Single(PackBuilder.ReadManifest(output).Operations).Source);
    }

    [Fact]
    public void ThePackHoldsOnlyTheManifestAndTheEditedFiles()
    {
        WriteWorkspace("icon/a.png", "textures/b.png");
        Edit("icon/a.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        PackBuilder.Build(_workspace, output);

        using var archive = ZipFile.OpenRead(output);
        Assert.Equal(["icon/a.png", PackManifest.FileName], archive.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public void TheBaselineHashDoesNotTravelInThePack()
    {
        WriteWorkspace("a.png");
        Edit("a.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        PackBuilder.Build(_workspace, output);

        Assert.Null(Assert.Single(PackBuilder.ReadManifest(output).Operations).BaselineSha256);
    }

    [Fact]
    public void TheAddressSurvivesTheRoundTrip()
    {
        WriteWorkspace("a.png");
        Edit("a.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        PackBuilder.Build(_workspace, output);

        var target = Assert.Single(PackBuilder.ReadManifest(output).Operations).Target;
        Assert.Equal(new AssetAddress("woi_0", "Texture2D", "a", 0, 42), target);
    }

    [Fact]
    public void APackFromAFutureFormatIsRefused()
    {
        var json = new PackManifest { Id = "x", Name = "x", FormatVersion = PackManifest.CurrentFormatVersion + 1 }
            .ToJson();
        var error = Assert.Throws<InvalidDataException>(() => PackManifest.Parse(json));
        Assert.Contains("format version", error.Message);
    }
}
