using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

/// Where an icon texture lives. `Container` is a bundle name, or a file under *_Data when the icon
/// is not in the bundle cache at all.
public sealed record IconLocation(string TextureName, string Container, string? AssetPath);

/// Resolves an item's icon texture.
///
/// Almost every icon is registered in the lookup table as `OfferIcons/&lt;slug&gt;_icon1_big`, so no
/// scan is needed. A handful of the newest weapons are only in resources.assets, which is read
/// lazily and only when one of those is asked for.
public sealed class IconResolver(BundleSet bundles, AssetLookup lookup)
{
    public const string Suffix = "_icon1_big";

    private Dictionary<string, string>? _dataFileTextures;

    public IconLocation? ForWeapon(WeaponRecord weapon)
        => Resolve(weapon.Slug) ?? Resolve(weapon.PrefabName);

    public IconLocation? Resolve(string slug)
    {
        var textureName = slug + Suffix;

        // The lookup table is already case-insensitive, which matters: the icon names do not agree
        // with the slugs on capitalisation ("Dragongun" against "DragonGun", "_Icon1_big" against
        // "_icon1_big"). An exact match would miss two dozen weapons.
        var path = "OfferIcons/" + textureName;
        if (lookup.BundleFor(path) is { } bundle) return new IconLocation(textureName, bundle, path);

        return DataFileTextures().TryGetValue(textureName, out var file)
            ? new IconLocation(textureName, file, null)
            : null;
    }

    private Dictionary<string, string> DataFileTextures()
    {
        if (_dataFileTextures is not null) return _dataFileTextures;

        _dataFileTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!bundles.Context.HasClassDatabase) return _dataFileTextures;

        foreach (var file in bundles.Game.EnumerateSerializedFiles())
        {
            if (!Path.GetFileName(file).StartsWith("resources.assets", StringComparison.OrdinalIgnoreCase))
                continue;
            var instance = bundles.Context.OpenSerializedFile(file);
            foreach (var info in instance.file.AssetInfos)
            {
                if (info.TypeId != (int)AssetClassID.Texture2D) continue;
                var name = bundles.Context.Deserialize(instance, info)?["m_Name"].AsString;
                if (!string.IsNullOrEmpty(name) && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                    _dataFileTextures.TryAdd(name, Path.GetFileName(file));
            }
        }
        return _dataFileTextures;
    }
}
