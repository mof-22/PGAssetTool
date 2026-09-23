using PGAssetTool.Core.Settings;

namespace PGAssetTool.Core.Tests;

/// Turning a name from the game, or from an author, into one Windows will take.
///
/// This was four copies, each knowing about a different way a name can be refused. The cases here
/// are the ones the copies disagreed about.
public class SafeNameTests
{
    [Fact]
    public void CharactersWindowsWillNotTakeBecomeUnderscores()
        => Assert.Equal("Weapon_25_map", SafeName.For("Weapon:25/map"));

    [Fact]
    public void AnOrdinaryNameIsLeftAlone()
        => Assert.Equal("Map_Beretta_A", SafeName.For("Map_Beretta_A"));

    /// Windows will not create a component ending in a dot or a space, and the exporter names
    /// directories from the game's own strings as well as files.
    [Theory]
    [InlineData("OfferIcons.", "OfferIcons")]
    [InlineData("OfferIcons ", "OfferIcons")]
    [InlineData(" Gloss2 ", "Gloss2")]
    [InlineData("Weapon834. .", "Weapon834")]
    public void TheEndsAreTidied(string given, string wanted)
        => Assert.Equal(wanted, SafeName.For(given));

    /// A name that comes back empty is a path that quietly means the folder above it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void ANameLeftWithNothingBecomesWhatTheCallerAsksFor(string given)
    {
        Assert.Equal("_", SafeName.For(given));
        Assert.Equal("unnamed", SafeName.For(given, "unnamed"));
        Assert.Equal("", SafeName.For(given, ""));
    }

    /// A name made only of characters Windows refuses is still a name once they are replaced, and
    /// two objects called '??' and '::' are then two different folders rather than one.
    [Fact]
    public void ANameOfNothingButRefusedCharactersBecomesUnderscores()
        => Assert.Equal("__", SafeName.For("??"));

    [Fact]
    public void TheInsideOfANameKeepsItsSpacesAndDots()
        => Assert.Equal("Wawes 1.old", SafeName.For("Wawes 1.old"));
}
