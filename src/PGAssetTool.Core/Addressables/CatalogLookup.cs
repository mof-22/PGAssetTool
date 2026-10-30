using PGAssetTool.Core.Catalog;

namespace PGAssetTool.Core.Addressables;

/// The question the item layer asks — "which bundle holds this logical path" — answered out of the
/// content catalog instead of the registry the old route keeps it in.
///
/// The two agree on what the paths are: of the 26,211 the old registry lists, 25,827 are keys the
/// catalog knows, and the catalog knows 87,060 in all. They disagree on the answer, because a
/// bundle is named differently on each route, which is why the lookup carries the question rather
/// than a table.
///
/// Answered lazily. Asking for all of them costs about half a second; an item asks for a handful.
public static class CatalogLookup
{
    /// Every key the catalog holds except the bundles' own.
    ///
    /// A bundle is addressed by its file name, so `wear_remote_assets_hats_hat_adamant_3_&lt;hash&gt;
    /// .bundle` is a key like any other — and the item layer, which finds a thing's belongings by
    /// looking for its id in a path, collected those as if they were assets. They are left out
    /// here rather than filtered downstream, because nothing downstream has any use for one.
    public static AssetLookup Of(ContentCatalog catalog)
        => new(catalog.Keys.Where(k => catalog.ByFileName(k) is null),
            path => Holder(catalog, path));

    /// A location names the key its dependencies are filed under, and the bundle holding the asset
    /// itself is the first of them — Addressables puts it there, and it held for every item
    /// measured. The order is kept rather than searched, because finding out which bundle names an
    /// asset path means opening each one.
    private static string? Holder(ContentCatalog catalog, string path)
    {
        foreach (var location in catalog.LocationsFor(path))
            if (location.Bundles.Count > 0) return location.Bundles[0].FileName;
        return null;
    }
}
