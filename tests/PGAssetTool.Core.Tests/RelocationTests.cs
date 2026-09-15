using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class RelocationTests
{
    private static PackOperation Replace(string container, string name, long pathId, string op = PackOperations.ReplaceTexture)
        => new()
        {
            Op = op,
            Target = new AssetAddress(container, "Texture2D", name, PathId: pathId),
            Source = $"textures/{name}.png",
        };

    [Fact]
    public void AnOperationIsSentWhereItsAssetWasFound()
    {
        var operation = Replace("ecw_6", "ultimatum", 3853476458556530626);
        var moved = new Dictionary<string, Relocated>
        {
            [Relocation.Key(operation.Target)] = new("ecw_4", "cccc"),
        };

        var followed = Relocation.Follow(operation, moved);

        Assert.Equal("ecw_4", followed.Target.Container);
        // Everything else about the target is the pack's, unchanged: the same asset, somewhere else.
        Assert.Equal(operation.Target with { Container = "ecw_4" }, followed.Target);
    }

    [Fact]
    public void AnAssetFoundNowhereLeavesTheOperationWhereThePackPutIt()
    {
        var operation = Replace("dw", "Weapon10_old_combat_rifle_map", -8651735307883010369);
        var moved = new Dictionary<string, Relocated> { [Relocation.Key(operation.Target)] = new(null, "aaaa") };

        Assert.Same(operation, Relocation.Follow(operation, moved));
    }

    [Fact]
    public void AModThatNeverMovedAnythingIsLeftAlone()
    {
        var operation = Replace("ecw_6", "ultimatum", 1);

        Assert.Same(operation, Relocation.Follow(operation, null));
        Assert.Same(operation, Relocation.Follow(operation, new Dictionary<string, Relocated>()));
    }

    [Fact]
    public void TheRecordIsOfTheTargetAsThePackNamesIt()
    {
        // Two operations for same-named assets in two bundles are two records, not one.
        Assert.NotEqual(
            Relocation.Key(new AssetAddress("ecw_6", "Texture2D", "map", PathId: 5)),
            Relocation.Key(new AssetAddress("ecw_7", "Texture2D", "map", PathId: 5)));

        Assert.NotEqual(
            Relocation.Key(new AssetAddress("ecw_6", "Texture2D", "map", 0, 5)),
            Relocation.Key(new AssetAddress("ecw_6", "Texture2D", "map", 1, 6)));
    }
}
