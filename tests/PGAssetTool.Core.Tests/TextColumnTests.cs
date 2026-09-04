using PGAssetTool.Cli;

namespace PGAssetTool.Core.Tests;

public class TextColumnTests
{
    [Theory]
    [InlineData("IronSword", 9)]
    [InlineData("ナイトソード", 12)]
    [InlineData("ギルデッド・ゲイズ", 18)]
    [InlineData("", 0)]
    public void WideCharactersCountAsTwoCells(string text, int expected)
    {
        Assert.Equal(expected, TextColumn.DisplayWidth(text));
    }

    [Fact]
    public void PadFillsToTheRequestedDisplayWidth()
    {
        Assert.Equal(20, TextColumn.DisplayWidth(TextColumn.Pad("ナイトソード", 20)));
        Assert.Equal(20, TextColumn.DisplayWidth(TextColumn.Pad("IronSword", 20)));
    }

    [Fact]
    public void PadLeavesOversizedTextUntouched()
    {
        Assert.Equal("ナイトソード", TextColumn.Pad("ナイトソード", 4));
    }

    [Fact]
    public void PadTreatsNullAsEmpty()
    {
        Assert.Equal("   ", TextColumn.Pad(null, 3));
    }
}
