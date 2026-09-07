using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

/// The manifest half of adding an asset: what a pack says, and what an older build makes of it.
public class AddedAssetTests
{
    private static PackOperation Add(string newId) => new()
    {
        Op = PackOperations.AddAsset,
        Target = new AssetAddress("ecw_34", "Shader", newId, 0, -2415237287598442480),
        Source = $"{newId}.dat",
        NewId = newId,
    };

    private static PackOperation Repoint(string newId) => new()
    {
        Op = PackOperations.ReplaceRaw,
        Target = new AssetAddress("ecw_34", "Material", "debugger_20_26_map_font", 0, -3606985276219058294),
        Source = "debugger_20_26_map_font.dat",
        Pointers = [new PointerFixup { Path = "m_Shader", NewId = newId }],
    };

    [Fact]
    public void AHandleAndItsPointersSurviveTheManifest()
    {
        var manifest = new PackManifest
        {
            Id = "cute",
            Name = "Cute",
            Operations = [Add("font_color_fix"), Repoint("font_color_fix")],
        };

        var read = PackManifest.Parse(manifest.ToJson());

        Assert.Equal("font_color_fix", read.Operations[0].NewId);
        var fixup = Assert.Single(read.Operations[1].Pointers);
        Assert.Equal("m_Shader", fixup.Path);
        Assert.Equal("font_color_fix", fixup.NewId);

        // The id the author's editor gave it is carried as a preference, not dropped.
        Assert.Equal(-2415237287598442480, read.Operations[0].Target.PathId);
    }

    [Fact]
    public void APackFromALaterBuildIsRefusedRatherThanHalfUnderstood()
    {
        var ahead = new PackManifest { Id = "x", Name = "X", FormatVersion = 99 };

        var refused = Assert.Throws<InvalidDataException>(() => PackManifest.Parse(ahead.ToJson()));
        Assert.Contains("99", refused.Message);
    }

    [Fact]
    public void AnOperationWithNoPointersCarriesAnEmptyListRatherThanNull()
    {
        // The applier walks this without checking, and a manifest written before pointers existed
        // has no such property at all.
        var read = PackManifest.Parse("""
            { "formatVersion": 1, "id": "x", "name": "X", "operations":
              [ { "op": "replaceTexture",
                  "target": { "container": "c", "class": "Texture2D", "name": "n" },
                  "source": "n.png" } ] }
            """);

        Assert.Empty(Assert.Single(read.Operations).Pointers);
        Assert.Null(read.Operations[0].NewId);
    }
}
