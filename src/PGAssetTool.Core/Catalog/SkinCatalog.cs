using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Catalog;

public sealed record SkinRecord(int Index, string Id, IReadOnlyList<string> MaterialPaths, string? LocalizationKey);

/// Weapon skins, assembled from three bundles: `ws_d` maps a weapon to its skin indexes,
/// `generated_data` turns those indexes into ids, and `weapon_skins_data` holds each skin's
/// definition keyed by that id.
public sealed class SkinCatalog
{
    private readonly Dictionary<int, List<int>> _skinIndexesByWeapon;
    private readonly Dictionary<int, string> _idBySkinIndex;
    private readonly Dictionary<string, SkinRecord> _byId;

    private SkinCatalog(
        Dictionary<int, List<int>> skinIndexesByWeapon,
        Dictionary<int, string> idBySkinIndex,
        Dictionary<string, SkinRecord> byId)
        => (_skinIndexesByWeapon, _idBySkinIndex, _byId) = (skinIndexesByWeapon, idBySkinIndex, byId);

    public static SkinCatalog Load(BundleSet bundles)
    {
        var byWeapon = new Dictionary<int, List<int>>();
        foreach (var e in bundles.MonoBehaviour("ws_d", "WeaponSkinsDataStorage")["WeaponToSkinsDatas"]["Array"].Children)
            byWeapon[e["weaponIndex"].AsInt] = e["skinIndexes"]["Array"].Children.Select(x => x.AsInt).ToList();

        var ids = new Dictionary<int, string>();
        foreach (var e in bundles.MonoBehaviour("generated_data", "WeaponSkinItemTypeIndices_GENERATED")["indices"]["Array"].Children)
            ids.TryAdd(e["index"].AsInt, e["id"].AsString);

        var definitions = new Dictionary<string, SkinRecord>(StringComparer.OrdinalIgnoreCase);
        var file = bundles.Open("weapon_skins_data");
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetsTools.NET.Extra.AssetClassID.MonoBehaviour) continue;
            var field = bundles.Context.Deserialize(file, info);
            if (field is null) continue;
            var id = field["m_Name"].AsString;
            var materials = field["materialPaths"]["Array"].Children.Select(c => c.AsString).ToList();
            var key = field["nameLocalizationKey"];
            definitions.TryAdd(id, new SkinRecord(0, id, materials, key.IsDummy ? null : key.AsString));
        }

        return new SkinCatalog(byWeapon, ids, definitions);
    }

    public IReadOnlyList<SkinRecord> ForWeapon(int weaponIndex)
    {
        if (!_skinIndexesByWeapon.TryGetValue(weaponIndex, out var indexes)) return [];
        var result = new List<SkinRecord>();
        foreach (var index in indexes)
        {
            if (!_idBySkinIndex.TryGetValue(index, out var id)) continue;
            result.Add(_byId.TryGetValue(id, out var definition)
                ? definition with { Index = index }
                : new SkinRecord(index, id, [], null));
        }
        return result;
    }
}
