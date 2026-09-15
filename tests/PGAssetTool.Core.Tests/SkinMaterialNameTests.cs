using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Core.Tests;

public class SkinMaterialNameTests
{
    private static AssetNode Material(string name, long pathId, string bundle = "ecw_7")
        => new(pathId, AssetClassID.Material, name, bundle);

    [Theory]
    // Every one of these is a skin in the game naming a material its model calls something else.
    [InlineData("Weapon917_plastic_instigator", "plastic_instigator")]
    [InlineData("plastic_instigator_map", "plastic_instigator")]
    [InlineData("Weapon1819_idols_slayer.asset", "idols_slayer")]
    [InlineData("Weapon281_multiverse_dragon_cosmos_mat", "multiverse_dragon_cosmos_mat")]
    [InlineData("Weapon834_corrupted_ultimatum_map", "corrupted_ultimatum")]
    public void ANameIsTakenPlainly(string name, string plain)
        => Assert.Equal(plain, WeaponResolver.Plainly(name));

    [Fact]
    public void AWordThatOnlyStartsLikeAPrefixIsLeftAlone()
    {
        Assert.Equal("Weapons_rack", WeaponResolver.Plainly("Weapons_rack"));
        Assert.Equal("Weapon_default", WeaponResolver.Plainly("Weapon_default"));
    }

    [Fact]
    public void TheOneMaterialThatIsPlainlyTheOneNamedIsFound()
    {
        var model = new[]
        {
            Material("plastic_instigator_map", 1), Material("player", 2), Material("Particle_Stack_AB", 3),
        };

        Assert.Equal(1, WeaponResolver.Alike(model, "Weapon917_plastic_instigator")?.PathId);
    }

    [Fact]
    public void TwoThatAreBothPlainlyItAreNeitherOfThem()
    {
        var model = new[] { Material("multiverse_dragon_mat", 1), Material("Weapon281_multiverse_dragon_mat", 2) };

        Assert.Null(WeaponResolver.Alike(model, "Weapon281/Weapon281_multiverse_dragon_mat"[10..]));
    }

    [Fact]
    public void TheSameMaterialReachedTwiceIsStillOne()
    {
        var model = new[] { Material("idols_slayer", 7), Material("idols_slayer", 7, "ECW_7") };

        Assert.Equal(7, WeaponResolver.Alike(model, "Weapon1819_idols_slayer.asset")?.PathId);
    }

    [Fact]
    public void NothingNamedFindsNothing()
        => Assert.Null(WeaponResolver.Alike([Material("player", 1)], ""));
}
