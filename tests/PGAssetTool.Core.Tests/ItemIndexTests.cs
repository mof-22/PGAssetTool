using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Tests;

public class ItemIndexTests
{
    [Theory]
    [InlineData(1, 1001)]
    [InlineData(25, 25001)]
    [InlineData(1000, 1000001)]
    [InlineData(1961, 1961001)]
    public void WeaponIndexIsOrdinalTimesScalePlusCategory(int weaponNumber, int expected)
    {
        Assert.Equal(expected, ItemIndex.ForWeapon(weaponNumber));
        Assert.Equal(weaponNumber, ItemIndex.Ordinal(expected));
        Assert.Equal(ItemCategory.Weapon, ItemIndex.Category(expected));
    }

    [Theory]
    [InlineData(430014, ItemCategory.WeaponSkin)]
    [InlineData(823019, ItemCategory.Consumable)]
    [InlineData(98002, ItemCategory.Wearable)]
    [InlineData(160031, ItemCategory.CraftSet)]
    public void CategoryComesFromTheIndexRemainder(int index, ItemCategory expected)
    {
        Assert.Equal(expected, ItemIndex.Category(index));
    }
}
