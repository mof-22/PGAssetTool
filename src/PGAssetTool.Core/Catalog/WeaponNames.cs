using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// Every name each weapon carries, in every language the game ships.
///
/// A player knows one weapon by one name, but not necessarily the one the tool is displaying: the
/// same gun is Ultimatum, 究極点 and Ультиматум depending on who is looking. Searching only the
/// language on screen means knowing the English name to find the English entry, which is exactly
/// the knowledge someone reaching for a search box does not have.
///
/// All eleven tables together are a third of a second to read and sixteen thousand strings to hold,
/// so there is no reason to make anyone choose.
public sealed class WeaponNames
{
    private readonly Dictionary<int, string[]> _byIndex;

    private WeaponNames(Dictionary<int, string[]> byIndex) => _byIndex = byIndex;

    public int Count => _byIndex.Values.Sum(n => n.Length);

    /// Built from tables already in hand, which is how tests supply one without a game.
    public static WeaponNames FromTables(ItemCatalog items, params Localization[] tables)
        => new(items.Weapons.ToDictionary(w => w.Index, w => tables
            .Select(t => t.Translate(w.LocalizationKey))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!));

    public static WeaponNames Load(BundleSet bundles, ItemCatalog items)
    {
        var tables = new List<Localization>();
        foreach (var (bundle, _) in GameCatalogs.Languages(bundles))
        {
            // One unreadable table is not worth losing the other ten over.
            try { tables.Add(Localization.Load(bundles, bundle)); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }

        var byIndex = new Dictionary<int, string[]>();
        foreach (var weapon in items.Weapons)
            byIndex[weapon.Index] = tables
                .Select(t => t.Translate(weapon.LocalizationKey))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

        return new WeaponNames(byIndex);
    }

    public IReadOnlyList<string> For(WeaponRecord weapon)
        => _byIndex.GetValueOrDefault(weapon.Index, []);

    public bool Matches(WeaponRecord weapon, string text)
        => For(weapon).Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase));
}
