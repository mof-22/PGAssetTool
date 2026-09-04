using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Tests;

public class ItemCatalogTests
{
    // Mirrors the shape of the real data: the in-game number and the prefab number are different
    // sequences, and one weapon's in-game number collides with another's prefab number.
    private static readonly ItemCatalog Catalog = ItemCatalog.FromRecords([
        new WeaponRecord(1257001, GameNumber: 819, PrefabNumber: 1257,
            "Weapon1257", "ectoplasmic_grenade_launcher", "ectoplasmic_grenade_launcher", "Key_1"),
        new WeaponRecord(819001, GameNumber: 401, PrefabNumber: 819,
            "Weapon819", "johnny_p", "johnny_p", "Key_2"),
        new WeaponRecord(1256001, GameNumber: 818, PrefabNumber: 1256,
            "Weapon1256", "MonsterHunterMechBody1", "MonsterHunterMechBody1", ""),
        new WeaponRecord(1949001, GameNumber: 1854, PrefabNumber: 1949,
            "Weapon1949", "gilded_gaze", "gilded_gaze", "Key_3"),
    ]);

    private static readonly Localization Japanese = Localization.FromTerms("l_ja", [
        new("Key_1", "エクトプラズム・グレネードランチャー"),
        new("Key_2", "ジョニー・P"),
        new("Key_3", "ギルデッド・ゲイズ"),
    ]);

    [Fact]
    public void ABareNumberIsTheInGameNumber()
    {
        Assert.Equal("Weapon1257", Catalog.Find("819")?.PrefabName);
        Assert.Equal("Weapon819", Catalog.Find("401")?.PrefabName);
    }

    [Fact]
    public void SearchingANumberDoesNotMatchPrefabNumbersAsText()
    {
        var hits = Catalog.Search("819").ToList();
        Assert.Equal(819, Assert.Single(hits).GameNumber);
    }

    [Fact]
    public void APrefabNameStillResolvesToItsOwnWeapon()
    {
        Assert.Equal(401, Catalog.Find("Weapon819")?.GameNumber);
        Assert.Equal(401, Assert.Single(Catalog.Search("Weapon819")).GameNumber);
    }

    [Fact]
    public void SlugLookupIsCaseInsensitive()
    {
        Assert.Equal(401, Catalog.Find("JOHNNY_P")?.GameNumber);
    }

    [Fact]
    public void WeaponsAreOrderedByInGameNumber()
    {
        Assert.Equal([401, 818, 819, 1854], Catalog.Weapons.Select(w => w.GameNumber));
    }

    [Fact]
    public void AWeaponWithNoLocalizationKeyIsHidden()
    {
        Assert.True(Catalog.Find("818")!.IsHidden);
        Assert.False(Catalog.Find("819")!.IsHidden);
    }

    [Fact]
    public void SearchFindsNothingInAnotherLanguageWithoutALocalization()
    {
        Assert.Empty(Catalog.Search("ギルデッド"));
    }

    [Fact]
    public void SearchMatchesTheDisplayNameWhenALocalizationIsGiven()
    {
        Assert.Equal(1854, Assert.Single(Catalog.Search("ギルデッド", Japanese)).GameNumber);
        Assert.Equal(819, Assert.Single(Catalog.Search("グレネード", Japanese)).GameNumber);
    }

    [Fact]
    public void ANumericQueryStaysExactEvenWithALocalization()
    {
        Assert.Equal(819, Assert.Single(Catalog.Search("819", Japanese)).GameNumber);
    }
}
