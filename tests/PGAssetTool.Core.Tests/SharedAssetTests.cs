using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Tests;

/// The prefab names here are the ones the game actually uses, taken from bhlw and ecw_0.
public class SharedAssetTests
{
    private static readonly AssetAddress Mesh = new("ecw_0", "Mesh", "Plasma_Pistol_Mesh", 0, null);

    [Fact]
    public void TwoWeaponsOnOneMeshIsWorthSaying()
    {
        // Plasma Pistol and Hot Plasma Pistol are built on the same model.
        var shared = SharedAssets.Check(Mesh, ["Weapon119", "Weapon120"]);

        Assert.NotNull(shared);
        Assert.Equal([119, 120], shared.Weapons);
    }

    [Fact]
    public void AWeaponAndItsOwnPreviewPrefabAreNotTwoWeapons()
    {
        // Every weapon has an `_info` prefab for the preview, and they share everything. Warning
        // about that would put a warning on almost every mod.
        Assert.Null(SharedAssets.Check(Mesh, ["Weapon132", "Weapon132_info"]));
    }

    [Fact]
    public void ASkinSharingWithTheWeaponItIsASkinOfIsNotWorthSaying()
        => Assert.Null(SharedAssets.Check(Mesh, ["Weapon1284", "Weapon1284_night_tempest"]));

    [Fact]
    public void ASkinOfOneWeaponReachingAnotherStillCounts()
    {
        var shared = SharedAssets.Check(Mesh, ["Weapon132", "Weapon132_info", "Weapon50"]);

        Assert.NotNull(shared);
        Assert.Equal([50, 132], shared.Weapons);
    }

    [Fact]
    public void SomethingUsedByOneWeaponIsNotShared()
        => Assert.Null(SharedAssets.Check(Mesh, ["Weapon25"]));

    [Fact]
    public void AnObjectNothingReachesIsNotShared()
    {
        // Icons live in bundles with no prefabs at all; the game loads them by path at runtime.
        Assert.Null(SharedAssets.Check(Mesh, []));
    }

    [Fact]
    public void PrefabsTheCatalogueCannotNameStillCountAsSeparateUsers()
    {
        // Unity's built-in particle material is reached by things that are not weapons at all.
        var shared = SharedAssets.Check(Mesh, ["Weapon411", "SomeProp", "AnotherProp"]);

        Assert.NotNull(shared);
        Assert.Equal([411], shared.Weapons);
        Assert.Contains("SomeProp", shared.Prefabs);
    }

    [Fact]
    public void RayPrefabsAreNumberedTheSameWay()
        => Assert.NotNull(SharedAssets.Check(Mesh, ["Ray119", "Ray120"]));
}
