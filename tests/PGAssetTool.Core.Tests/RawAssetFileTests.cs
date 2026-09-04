using PGAssetTool.Core.RawAssets;

namespace PGAssetTool.Core.Tests;

public class RawAssetFileTests
{
    [Theory]
    // Path ids are signed, so the separator before a negative one reads as a double dash.
    [InlineData("cyber_lantern_icon1_big-CAB-97f664a29971b465dbed4e2dff6d4adb--5019533296712506245.dat",
        "cyber_lantern_icon1_big", "CAB-97f664a29971b465dbed4e2dff6d4adb", -5019533296712506245L)]
    [InlineData("Barret_3_map-CAB-9323583183c9aa05dfd30fa40fdd6030-1917735387879604778.dat",
        "Barret_3_map", "CAB-9323583183c9aa05dfd30fa40fdd6030", 1917735387879604778L)]
    // Asset names carry spaces and brackets, and sometimes dashes of their own.
    [InlineData("L2 (4)-CAB-b80caa65b0b4d0782fc3380a1a2d9799--22456288143565344.dat",
        "L2 (4)", "CAB-b80caa65b0b4d0782fc3380a1a2d9799", -22456288143565344L)]
    [InlineData("anti-champion-rifle-CAB-abc123-42.dat",
        "anti-champion-rifle", "CAB-abc123", 42L)]
    public void TheFileNameCarriesTheAssetsAddress(string fileName, string name, string cab, long pathId)
    {
        Assert.True(RawAssetFile.TryParse(fileName, out var parsed));
        Assert.Equal(name, parsed.Name);
        Assert.Equal(cab, parsed.Cab);
        Assert.Equal(pathId, parsed.PathId);
    }

    [Theory]
    [InlineData("plain.dat")]
    [InlineData("no-path-id-CAB-abc123.dat")]
    [InlineData("CAB-abc123-42.dat")]          // no asset name in front
    [InlineData("name-NOTCAB-abc123-42.dat")]
    public void AnythingElseIsNotARawAsset(string fileName)
    {
        Assert.False(RawAssetFile.TryParse(fileName, out _));
    }

    [Fact]
    public void TheLastCabMarkerWins()
    {
        // A name that itself looks like a marker must not shadow the real one.
        Assert.True(RawAssetFile.TryParse("CAB-decoy-CAB-abc123-7.dat", out var parsed));
        Assert.Equal("CAB-decoy", parsed.Name);
        Assert.Equal("CAB-abc123", parsed.Cab);
        Assert.Equal(7L, parsed.PathId);
    }
}
