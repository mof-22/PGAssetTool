using System.Text.Json;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// Everything the game sells that is not a weapon: hats, capes, masks, boots, armor, pets, gliders,
/// transports, avatars.
///
/// Three registries between them, all in `generated_data` and all embedded JSON rather than fields:
/// `Wear_GENERATED` for the five things a player wears, `Pets_GENERATED` for pets, and
/// `Royale_GENERATED` for the battle royale cosmetics, which are told apart by a category number.
/// Reading them costs one bundle and about a fifth of a second, so it happens beside the weapons.
///
/// What comes out is an ItemRecord like any other, so the resolver, the tree, the preview and the
/// exporter do not need to know a hat from a rocket launcher.
public sealed class GearCatalog
{
    private readonly Dictionary<string, List<WeaponRecord>> _byKind;

    private GearCatalog(Dictionary<string, List<WeaponRecord>> byKind) => _byKind = byKind;

    public static GearCatalog Empty { get; } = new([]);

    /// Which wear category in the game's own registry is which kind here.
    private static readonly Dictionary<string, ItemKind> Worn = new(StringComparer.Ordinal)
    {
        ["HatsCategory"] = ItemKinds.Hat,
        ["CapesCategory"] = ItemKinds.Cape,
        ["MaskCategory"] = ItemKinds.Mask,
        ["BootsCategory"] = ItemKinds.Boots,

        // ArmorCategory is left out: it has 32 entries and not one prefab anywhere in the game, so
        // there would be nothing behind any of them to open.
    };

    /// The battle royale registry holds five categories in one table, and the number is the only
    /// thing that says which is which. Trails and the royale hat set are left out: neither is a
    /// model an author can open, and both would be a list of names with nothing behind them.
    private static readonly Dictionary<int, ItemKind> Royale = new()
    {
        [160000] = ItemKinds.Glider,
        [190000] = ItemKinds.Avatar,
        [210000] = ItemKinds.Transport,
    };

    public static GearCatalog Load(BundleSet bundles)
    {
        var byKind = new Dictionary<string, List<WeaponRecord>>(StringComparer.OrdinalIgnoreCase);

        void Add(ItemKind kind, string id, string? key, string readable, int index)
        {
            if (id.Length == 0) return;
            if (!byKind.TryGetValue(kind.Name, out var list)) byKind[kind.Name] = list = [];
            if (list.Any(r => string.Equals(r.Slug, id, StringComparison.OrdinalIgnoreCase))) return;

            list.Add(new WeaponRecord(
                Index: index,
                GameNumber: 0,
                PrefabNumber: 0,
                PrefabName: id,
                Slug: id,
                Tag: readable,
                LocalizationKey: key ?? "")
            {
                Kind = kind,
            });
        }

        try
        {
            ReadWear(bundles, Add);
            ReadPets(bundles, Add);
            ReadRoyale(bundles, Add);
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            // A registry that will not read costs its kinds and nothing else: the weapons, which
            // are read from somewhere else entirely, are what the tool is for.
        }

        foreach (var list in byKind.Values)
            list.Sort((a, b) => string.Compare(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase));

        return new GearCatalog(byKind);
    }

    /// The five worn kinds. Ids and categories are in one table and the descriptions in another,
    /// keyed by id — and the ids come as groups, because a cape and its two upgrade tiers are three
    /// entries the shop shows as one. All three are separate things to an author.
    private static void ReadWear(BundleSet bundles, Action<ItemKind, string, string?, string, int> add)
    {
        var wear = bundles.MonoBehaviour("generated_data", "Wear_GENERATED");
        using var ids = JsonDocument.Parse(wear["DictionaryWearIdsJson"].AsString);
        using var infos = JsonDocument.Parse(wear["DictionaryWearInfoByIdJson"].AsString);

        foreach (var category in ids.RootElement.EnumerateObject())
        {
            if (!Worn.TryGetValue(category.Name, out var kind)) continue;

            foreach (var group in category.Value.EnumerateArray())
            foreach (var entry in group.EnumerateArray())
            {
                var id = entry.GetString() ?? "";
                if (id.Length == 0) continue;

                var known = infos.RootElement.TryGetProperty(id, out var info);
                add(kind, id,
                    known ? Text(info, "TitleLocalizeKey") : null,
                    known ? Text(info, "ReadableName") : "",
                    known && info.TryGetProperty("Index", out var at) ? at.GetInt32() : 0);
            }
        }
    }

    private static void ReadPets(BundleSet bundles, Action<ItemKind, string, string?, string, int> add)
    {
        using var pets = JsonDocument.Parse(
            bundles.MonoBehaviour("generated_data", "Pets_GENERATED")["InfoJson"].AsString);

        foreach (var pet in pets.RootElement.EnumerateObject())
            add(ItemKinds.Pet, pet.Name, Text(pet.Value, "Lkey"), "", 0);
    }

    private static void ReadRoyale(BundleSet bundles, Action<ItemKind, string, string?, string, int> add)
    {
        using var royale = JsonDocument.Parse(
            bundles.MonoBehaviour("generated_data", "Royale_GENERATED")["InfoJson"].AsString);

        foreach (var item in royale.RootElement.EnumerateObject())
        {
            if (!item.Value.TryGetProperty("Category", out var category)) continue;
            if (!Royale.TryGetValue(category.GetInt32(), out var kind)) continue;

            add(kind, item.Name, Text(item.Value, "LKey"), "",
                item.Value.TryGetProperty("Index", out var at) ? at.GetInt32() : 0);
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public IReadOnlyList<WeaponRecord> Of(ItemKind kind)
        => _byKind.GetValueOrDefault(kind.Name) ?? [];

    /// How many of each kind there are, for saying so without listing them.
    public int Count(ItemKind kind) => Of(kind).Count;
}
