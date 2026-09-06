using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// What an asset is called, for the classes that do not keep it where everything else does.
///
/// Almost every class carries `m_Name` at the top, and reading that is the whole rule. A Shader
/// does not: every one of the game's fifteen in ecw_34 has an empty `m_Name`, and the name that
/// materials, the editor and an author all use — `Unlit/Text`, `Optimized/MultiVFX` — is one level
/// down in `m_ParsedForm`. Addressing shaders by the top-level field would file them all under the
/// same empty name and leave the ordinal as the only thing telling them apart.
public static class AssetNaming
{
    public static string NameOf(AssetTypeValueField? field, AssetClassID cls)
    {
        if (field is null) return "";

        if (cls == AssetClassID.Shader && field["m_ParsedForm"] is { IsDummy: false } parsed
            && parsed["m_Name"] is { IsDummy: false } parsedName)
            return parsedName.AsString ?? "";

        return field["m_Name"] is { IsDummy: false } name ? name.AsString ?? "" : "";
    }

    /// Writes a new name into the asset, in the same place NameOf reads it from. False when the
    /// class keeps its name somewhere this does not know about, so a caller can say so rather than
    /// quietly leaving the old one.
    public static bool TryRename(AssetTypeValueField field, AssetClassID cls, string name)
    {
        var holder = cls == AssetClassID.Shader && field["m_ParsedForm"] is { IsDummy: false } parsed
            ? parsed
            : field;

        if (holder["m_Name"] is not { IsDummy: false } target) return false;
        target.AsString = name;
        return true;
    }
}
