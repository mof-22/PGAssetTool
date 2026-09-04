using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// Every item in the game carries an index of the form `ordinal * 1000 + categoryCode`.
public enum ItemCategory
{
    Weapon = 1,
    Wearable = 2,
    WeaponSkin = 14,
    Consumable = 19,
    Cape = 21,
    WearableSkin = 29,
    CraftSet = 31,
}

public static class ItemIndex
{
    public const int Scale = 1000;

    public static int For(int ordinal, ItemCategory category) => ordinal * Scale + (int)category;
    public static int Ordinal(int index) => index / Scale;
    public static ItemCategory Category(int index) => (ItemCategory)(index % Scale);

    /// Weapon 25 lives at index 25001 and its prefab is named "Weapon25".
    public static int ForWeapon(int weaponNumber) => For(weaponNumber, ItemCategory.Weapon);
}

public sealed record WeaponRecord(
    int Index,
    int WeaponNumber,
    string PrefabName,
    string Slug,
    string Tag,
    string LocalizationKey);

/// The weapon registry in the `it_d` bundle: `itemDatas` gives every item a slug, `itemRecords`
/// carries the weapon-specific fields.
public sealed class ItemCatalog
{
    private readonly Dictionary<int, WeaponRecord> _byIndex;

    private ItemCatalog(Dictionary<int, WeaponRecord> byIndex) => _byIndex = byIndex;

    public static ItemCatalog Load(BundleSet bundles)
    {
        var storage = bundles.MonoBehaviour("it_d", "ItemsDataStorage");

        var slugs = new Dictionary<int, string>();
        foreach (var e in storage["itemDatas"]["Array"].Children)
            if (ItemIndex.Category(e["index"].AsInt) == ItemCategory.Weapon)
                slugs.TryAdd(e["index"].AsInt, e["id"].AsString);

        var byIndex = new Dictionary<int, WeaponRecord>();
        foreach (var e in storage["itemRecords"]["Array"].Children)
        {
            int index = e["_index"].AsInt;
            var prefab = e["_prefabName"].AsString;
            byIndex.TryAdd(index, new WeaponRecord(
                Index: index,
                WeaponNumber: ItemIndex.Ordinal(index),
                PrefabName: prefab,
                Slug: slugs.GetValueOrDefault(index, prefab),
                Tag: e["_tag"].AsString,
                LocalizationKey: e["localizeWeaponKey"].AsString));
        }
        return new ItemCatalog(byIndex);
    }

    public int Count => _byIndex.Count;
    public IEnumerable<WeaponRecord> Weapons => _byIndex.Values.OrderBy(w => w.WeaponNumber);

    public WeaponRecord? ByIndex(int index) => _byIndex.GetValueOrDefault(index);
    public WeaponRecord? ByNumber(int weaponNumber) => ByIndex(ItemIndex.ForWeapon(weaponNumber));

    /// Accepts a weapon number, a prefab name ("Weapon25"), or a slug ("Beretta").
    public WeaponRecord? Find(string query)
    {
        if (int.TryParse(query, out var number) && ByNumber(number) is { } byNumber) return byNumber;
        return _byIndex.Values.FirstOrDefault(w =>
                   string.Equals(w.PrefabName, query, StringComparison.OrdinalIgnoreCase))
            ?? _byIndex.Values.FirstOrDefault(w =>
                   string.Equals(w.Slug, query, StringComparison.OrdinalIgnoreCase));
    }

    /// Matches against the slug, tag and prefab name, which are always English, plus the display
    /// name when a localization is supplied — otherwise a search in any other language finds nothing.
    public IEnumerable<WeaponRecord> Search(string text, Localization? localization = null)
        => Weapons.Where(w =>
            w.Slug.Contains(text, StringComparison.OrdinalIgnoreCase)
            || w.Tag.Contains(text, StringComparison.OrdinalIgnoreCase)
            || w.PrefabName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || localization?.Translate(w.LocalizationKey) is { } name
               && name.Contains(text, StringComparison.OrdinalIgnoreCase));
}
