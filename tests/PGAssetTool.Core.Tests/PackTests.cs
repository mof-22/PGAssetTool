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
    public void AWorkspaceOfAlreadyModifiedFilesPacksWithoutBeingEdited()
    {
        // Converting an existing mod produces files that are the modification already. Waiting for
        // an edit that has happened would leave nothing to pack.
        var path = System.IO.Path.Combine(_workspace, "a.png");
        File.WriteAllText(path, "the mod's own artwork");
        File.WriteAllText(System.IO.Path.Combine(_workspace, PackManifest.FileName),
            new PackManifest
            {
                Id = "converted",
                Name = "converted",
                Operations =
                [
                    new PackOperation
                    {
                        Op = PackOperations.ReplaceTexture,
                        Target = new AssetAddress("ecw_0", "Texture2D", "a", 0, 1),
                        Source = "a.png",
                        BaselineSha256 = null,
                    },
                ],
            }.ToJson());

        var output = System.IO.Path.Combine(_workspace, "out.pgmod");
        Assert.Equal(1, PackBuilder.Build(_workspace, output).Operations);
    }

    [Fact]
    public void APackFromAFutureFormatIsRefused()
    {
        var json = new PackManifest { Id = "x", Name = "x", FormatVersion = PackManifest.CurrentFormatVersion + 1 }
            .ToJson();
        var error = Assert.Throws<InvalidDataException>(() => PackManifest.Parse(json));
        Assert.Contains("format version", error.Message);
    }

    [Fact]
    public void TheIconTravelsWithThePackEvenWhenItIsNotBeingReplaced()
    {
        // The icon is what the pack looks like, not part of what it does — so it goes in whether or
        // not it is one of the files being written to the game.
        var manifest = WriteWorkspace("textures/a.png", "icon/big.png");
        Workspace.Save(_workspace, manifest with { Icon = "icon/big.png" });
        Edit("textures/a.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        var built = PackBuilder.Build(_workspace, output);

        Assert.Equal(1, built.Operations);
        Assert.Equal("icon/big.png", PackBuilder.ReadManifest(output).Icon);
        Assert.Equal("original icon/big.png",
            System.Text.Encoding.UTF8.GetString(PackBuilder.ReadIcon(output)!));
    }

    [Fact]
    public void APackThatNamesAnIconItNoLongerHasSaysItHasNone()
    {
        // Claiming a picture that is not in the file would be a broken pack rather than one without
        // a picture, and whatever opened it would have to guess which.
        var manifest = WriteWorkspace("textures/a.png");
        Workspace.Save(_workspace, manifest with { Icon = "icon/gone.png" });
        Edit("textures/a.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        PackBuilder.Build(_workspace, output);

        Assert.Equal("", PackBuilder.ReadManifest(output).Icon);
        Assert.Null(PackBuilder.ReadIcon(output));
    }

    [Fact]
    public void APackWithNoIconAtAllReadsBackAsHavingNone()
    {
        WriteWorkspace("textures/a.png");
        Edit("textures/a.png");

        var output = Path.Combine(_workspace, "out.pgmod");
        PackBuilder.Build(_workspace, output);

        Assert.Null(PackBuilder.ReadIcon(output));
    }

    [Fact]
    public void SomethingThatIsNotAPackDoesNotThrowWhenAskedForItsIcon()
    {
        var notAPack = Path.Combine(_workspace, "rubbish.pgmod");
        File.WriteAllText(notAPack, "not a zip");

        Assert.Null(PackBuilder.ReadIcon(notAPack));
        Assert.Null(PackBuilder.ReadIcon(Path.Combine(_workspace, "nothing here.pgmod")));
    }

    [Fact]
    public void ThePackIsNamedAfterTheModRatherThanTheDirectory()
    {
        // Three names for one thing — the folder, the manifest's name, and the id — each
        // authoritative somewhere different: the manager showed one and the file carried another.
        var manifest = WriteWorkspace("textures/a.png");
        Workspace.Save(_workspace, manifest with { Name = "Synthwave Beretta" });

        Assert.Equal("Synthwave Beretta.pgmod", PackBuilder.FileNameFor(Workspace.Read(_workspace)));
        Assert.Equal(
            Path.Combine(_workspace, "Synthwave Beretta.pgmod"),
            PackBuilder.OutputFor(_workspace, Workspace.Read(_workspace)));
    }

    [Theory]
    [InlineData("Ultimatum", "Ultimatum.pgmod")]
    [InlineData("what/now?", "what_now_.pgmod")]
    [InlineData("  spaced  ", "spaced.pgmod")]
    [InlineData("trailing.", "trailing.pgmod")]
    public void AModNameThatWouldNotDoAsAFileNameIsMadeIntoOne(string name, string expected)
    {
        // Windows refuses a name ending in a dot, and a slash would put the pack somewhere else
        // entirely — which is the kind of surprise a build should never spring on anyone.
        Assert.Equal(expected, PackBuilder.FileNameFor(new PackManifest { Id = "x", Name = name }));
    }

    [Fact]
    public void AModWithNoNameFallsBackToItsIdRatherThanToNothing()
    {
        Assert.Equal("beretta.pgmod", PackBuilder.FileNameFor(new PackManifest { Id = "beretta", Name = "" }));
        Assert.Equal("mod.pgmod", PackBuilder.FileNameFor(new PackManifest { Id = "...", Name = "   " }));
    }

    [Fact]
    public void ExtractingTheSameThingTwiceDoesNotWriteOverTheFirst()
    {
        // Two mods of one weapon are two mods, so two workspaces are two directories. Writing over
        // the first took an author's edits with it, and the manifest that said which mod they were.
        var wanted = Path.Combine(_workspace, "0416_ultimatum");

        Assert.Equal(wanted, Workspace.Free(wanted));

        Directory.CreateDirectory(wanted);
        File.WriteAllText(Path.Combine(wanted, PackManifest.FileName), "{}");
        Assert.Equal(wanted + "_2", Workspace.Free(wanted));

        Directory.CreateDirectory(wanted + "_2");
        File.WriteAllText(Path.Combine(wanted + "_2", PackManifest.FileName), "{}");
        Assert.Equal(wanted + "_3", Workspace.Free(wanted));
    }

    [Fact]
    public void ADirectoryThatIsNotAWorkspaceIsNotSteppedOver()
    {
        // Present is not the same as somebody's. An empty folder left behind by a run that failed
        // half way is a folder to write into, not one to number past.
        var wanted = Path.Combine(_workspace, "0016_beretta");
        Directory.CreateDirectory(wanted);
        File.WriteAllText(Path.Combine(wanted, "leftover.png"), "not a manifest");

        Assert.Equal(wanted, Workspace.Free(wanted));
    }
}
