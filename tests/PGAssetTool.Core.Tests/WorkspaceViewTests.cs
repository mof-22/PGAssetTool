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

    [Fact]
    public void RenamingMovesTheDirectoryAndLeavesTheContentsAlone()
    {
        // The folder name is what the built .pgmod is called, so this is how an author names their
        // mod rather than living with the number and prefab the export chose.
        var directory = MakeWorkspace("0016_Beretta", ("icon/a.png", "one"), ("textures/b.png", "two"));

        var moved = Workspace.Rename(directory, "Synthwave Beretta");

        Assert.Equal(Path.Combine(_root, "Synthwave Beretta"), moved);
        Assert.False(Directory.Exists(directory));
        Assert.Equal("one", File.ReadAllText(Path.Combine(moved, "icon", "a.png")));
        Assert.Equal(2, WorkspaceView.Open(moved)!.Files.Count);
    }

    [Fact]
    public void RenamingOntoAnExistingWorkspaceIsRefusedRatherThanMerged()
    {
        var mine = MakeWorkspace("0016_Beretta", ("icon/a.png", "mine"));
        MakeWorkspace("theirs", ("icon/a.png", "theirs"));

        Assert.Contains("already a workspace",
            Assert.Throws<IOException>(() => Workspace.Rename(mine, "theirs")).Message);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(mine, "icon", "a.png")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("what?")]
    public void ANameThatCannotBeAFolderIsRefused(string name)
    {
        var directory = MakeWorkspace("0016_Beretta", ("icon/a.png", "one"));
        Assert.Throws<ArgumentException>(() => Workspace.Rename(directory, name));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ChangingOnlyTheCapitalisationIsStillARename()
    {
        // The directory it "already exists" as is the one being renamed, so the collision check has
        // to let this one through.
        var directory = MakeWorkspace("beretta", ("icon/a.png", "one"));

        var moved = Workspace.Rename(directory, "Beretta");

        Assert.Equal("Beretta", Path.GetFileName(moved));
        Assert.Equal("Beretta", new DirectoryInfo(moved).Name);
    }

    [Fact]
    public void SavingTheManifestKeepsTheOperationsAndTheirBaselines()
    {
        // Only the descriptive half is ever edited by hand; the operations are addresses the export
        // resolved, and losing a baseline would make every unedited file look changed.
        var directory = MakeWorkspace("0016_Beretta", ("icon/a.png", "one"), ("textures/b.png", "two"));
        var before = Workspace.Read(directory);

        Workspace.Save(directory, before with
        {
            Name = "Synthwave Beretta", Author = "mof-22", Version = "2.1.0", Description = "neon",
        });

        var after = Workspace.Read(directory);
        Assert.Equal("Synthwave Beretta", after.Name);
        Assert.Equal("mof-22", after.Author);
        Assert.Equal("2.1.0", after.Version);
        Assert.Equal("neon", after.Description);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(
            before.Operations.Select(o => (o.Source, o.BaselineSha256)),
            after.Operations.Select(o => (o.Source, o.BaselineSha256)));
        Assert.Empty(Workspace.Changed(directory, after));
    }
}
