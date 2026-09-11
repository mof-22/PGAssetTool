using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Catalog;

/// A sort of thing the game sells, and where it files the parts of one.
///
/// Weapons are the kind this tool grew up around, and every other kind turns out to be the same
/// arrangement under different names: a prefab under a root of its own, a skin beside it under
/// another, an offer icon named after the id. Saying that once here is what lets the resolver, the
/// tree, the preview and the exporter stay as they are.
///
/// <param name="Name">What the picker calls it, in the singular.</param>
/// <param name="Pack">
/// What a pack built from it is filed under: lower case, because it is a folder name as much as a
/// word. See PackSubject.
/// </param>
/// <param name="Roots">
/// Where a prefab of this kind sits, best first. More than one because the game moved some of them
/// and left the rest: boots are under `Boots/Prefabs`, except for the dozen oldest, which are still
/// directly under `Boots`. Whichever the lookup table actually has is the one used.
/// </param>
/// <param name="SkinRoot">
/// Where this kind's skins live, for the kinds that have them. A hat's skin is
/// `HatsSkins/skin_<id>` against the hat's own `Hats/<id>`; a pet or a glider has none.
/// </param>
/// <param name="Suffix">
/// What the game adds to the id to name the prefab. A glider's model is `carpet_plane_game` where
/// its id is `carpet_plane`, and nothing else needs one.
/// </param>
public sealed record ItemKind(
    string Name, string Pack, IReadOnlyList<string> Roots, string? SkinRoot = null, string Suffix = "")
{
    public ItemKind(string name, string pack, string root, string? skinRoot = null, string suffix = "")
        : this(name, pack, [root], skinRoot, suffix) { }

    /// Where the prefab for one of these might live, by its id, best first.
    public IEnumerable<string> PathsFor(string id) => Roots.Select(root => $"{root}/{id}{Suffix}");

    /// Where it lives if the game has not moved it, which is what a pack records.
    public string PathFor(string id) => $"{Roots[0]}/{id}{Suffix}";

    /// Where its skin lives, for a kind that has skins.
    public string? SkinPathFor(string id) => SkinRoot is null ? null : $"{SkinRoot}/skin_{id}";

    public override string ToString() => Name;
}

/// Every kind the tool can browse.
///
/// Eight of the eleven the author asked for, and the three that are missing are missing from the
/// game rather than from here. Graffiti is not something it files anywhere — nothing in the lookup
/// table is one. Gadgets amount to two prefabs with no registry behind them. And armor has 32
/// entries in the registry and not one prefab: an armor is a number and a shop icon, and the icon
/// lives in a data file rather than a bundle, which is somewhere this tool does not yet write.
public static class ItemKinds
{
    /// The kind everything else was built around: numbered, skinned, and with related assets filed
    /// by the number in its prefab name rather than by its id.
    public static readonly ItemKind Weapon = new("Weapon", PackKind.Weapon, ["Weapons"]);

    public static readonly ItemKind Hat = new("Hat", "hat", ["Hats/Prefabs", "Hats"], "HatsSkins");
    public static readonly ItemKind Cape = new("Cape", "cape", ["Capes/Prefabs", "Capes"], "CapesSkins");
    public static readonly ItemKind Mask = new("Mask", "mask", ["Masks/Prefabs", "Masks"], "MasksSkins");
    public static readonly ItemKind Boots = new("Boots", "boots", ["Boots/Prefabs", "Boots"], "BootsSkins");
    public static readonly ItemKind Pet = new("Pet", "pet", ["Pets/Content"]);
    public static readonly ItemKind Glider = new("Glider", "glider", ["Gliders"], Suffix: "_game");
    public static readonly ItemKind Avatar = new("Avatar", "avatar", ["RoyaleAvatars"]);

    /// Cars, hovercraft and one helicopter, each parked under the kind of vehicle it is.
    public static readonly ItemKind Transport = new("Transport", "transport", [
        "VehicleBattleRoyale/Vehicle_Battle",
        "VehicleBattleRoyale/HoverCar",
        "VehicleBattleRoyale/Helicopter_prefabs/Views",
    ]);

    public static IReadOnlyList<ItemKind> All { get; } =
        [Weapon, Hat, Cape, Mask, Boots, Pet, Glider, Transport, Avatar];

    public static ItemKind? ByName(string name)
        => All.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(k => string.Equals(k.Pack, name, StringComparison.OrdinalIgnoreCase));
}
