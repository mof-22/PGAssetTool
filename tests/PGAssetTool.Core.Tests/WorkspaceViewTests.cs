using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class WorkspaceViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-view").FullName;

    /// A workspace as `extract --workspace` leaves one: files plus a manifest naming their targets.
    private string MakeWorkspace(string name, params (string Path, string Content)[] files)
    {
        var directory = Path.Combine(_root, name);
        var assets = new List<ExportedAsset>();

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);

            assets.Add(new ExportedAsset(full, AssetsTools.NET.Extra.AssetClassID.Texture2D,
                Path.GetFileNameWithoutExtension(relative), Path.GetExtension(relative).TrimStart('.'),
                content.Length, new AssetAddress("bhlw", "Texture2D", Path.GetFileNameWithoutExtension(relative))));
        }

        Workspace.Create(directory, name, name, "tester", "26.11.0", assets);
        return directory;
    }

    [Fact]
    public void AFreshWorkspaceHasNothingEdited()
    {
        var directory = MakeWorkspace("w1", ("textures/a.png", "original"), ("textures/b.png", "original"));

        var view = WorkspaceView.Open(directory);

        Assert.NotNull(view);
        Assert.Equal(2, view.Files.Count);
        Assert.Equal(0, view.EditedCount);
    }

    [Fact]
    public void EditingAFileShowsUpOnTheNextRead()
    {
        // The files are edited by other programs, so the answer has to be taken when asked rather
        // than remembered from when the workspace was written.
        var directory = MakeWorkspace("w2", ("textures/a.png", "original"), ("textures/b.png", "original"));
        Assert.Equal(0, WorkspaceView.Open(directory)!.EditedCount);

        File.WriteAllText(Path.Combine(directory, "textures", "a.png"), "painted over");

        var view = WorkspaceView.Open(directory)!;
        Assert.Equal(1, view.EditedCount);
        Assert.True(view.Files.Single(f => f.Name == "a.png").Edited);
        Assert.False(view.Files.Single(f => f.Name == "b.png").Edited);
    }

    [Fact]
    public void EditingAFileBackToWhatItWasCountsAsUnedited()
    {
        // The hash decides, not the timestamp: undoing an edit has to leave the file out of the
        // pack, or a pack would contain operations that change nothing.
        var directory = MakeWorkspace("w3", ("textures/a.png", "original"));
        var path = Path.Combine(directory, "textures", "a.png");

        File.WriteAllText(path, "painted over");
        Assert.Equal(1, WorkspaceView.Open(directory)!.EditedCount);

        File.WriteAllText(path, "original");
        Assert.Equal(0, WorkspaceView.Open(directory)!.EditedCount);
    }

    [Fact]
    public void EveryWorkspaceUnderTheRootIsFound()
    {
        MakeWorkspace("0016_Beretta", ("textures/a.png", "x"));
        MakeWorkspace("0058_Plasma", ("textures/b.png", "y"));
        Directory.CreateDirectory(Path.Combine(_root, "not-a-workspace"));

        var found = WorkspaceView.Discover(_root);

        Assert.Equal(2, found.Count);
        Assert.All(found, d => Assert.True(File.Exists(Path.Combine(d, PackManifest.FileName))));
    }

    [Fact]
    public void ARootThatDoesNotExistYetIsEmptyRatherThanAnError()
        => Assert.Empty(WorkspaceView.Discover(Path.Combine(_root, "never-created")));

    [Fact]
    public void ADirectoryWithNoManifestOpensAsNothing()
    {
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);

        Assert.Null(WorkspaceView.Open(bare));
    }

    [Fact]
    public void FilesAreGroupedByFolderAndNamed()
    {
        var directory = MakeWorkspace("w4", ("meshes/z.png", "1"), ("textures/a.png", "2"));

        var view = WorkspaceView.Open(directory)!;

        Assert.Equal(["meshes", "textures"], view.Files.Select(f => f.Folder));
        Assert.Equal(["z.png", "a.png"], view.Files.Select(f => f.Name));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("icon/Beretta_icon1_big.png", true)]
    [InlineData("related/WeaponChatIcons/Weapon25_chaticon.png", true)]
    [InlineData("textures/Map_Beretta_A.png", false)]
    [InlineData("meshes/Beretta_3_Mesh.glb", false)]
    public void OnlyIconsMeanCoverageByTheirAlphaChannel(string relativePath, bool coverage)
    {
        // A model texture keeps emission there, so honouring it blanks the picture; an icon really
        // is cut out. The folder is what the workspace records, so the folder is what decides.
        var file = new WorkspaceFile(
            relativePath, relativePath, new AssetAddress("", "", ""), "replaceTexture", false, 0);

        Assert.Equal(coverage, file.AlphaIsCoverage);
    }
}
