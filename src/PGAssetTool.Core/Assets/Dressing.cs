using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// What a mesh is drawn wearing: the main texture of every material bound where it is used.
///
/// How Unity pairs a mesh with its paint is in two places and neither is on the mesh. A renderer
/// names the materials, one per submesh, and a material names the texture in a slot called
/// `_MainTex` — so answering "what does this model look like" means finding the renderer that draws
/// it and following two more pointers, either of which may lead into another bundle.
///
/// The weapon tree asks this of a prefab's own closure, which is the right question there: it knows
/// which renderers belong to the weapon. The editor has no weapon — only a file it is showing and
/// the address that file came from — so it asks the same thing the other way round, of everything
/// in the bundle: which renderer draws this mesh.
public sealed class Dressing(BundleSet bundles, BundleGraph? graph = null)
{
    private readonly BundleGraph _graph = graph ?? new BundleGraph(bundles);

    /// The graph this followed the pointers with, offered so a caller that also walks between
    /// bundles does not build a second index of which bundle holds which file.
    public BundleGraph Graph => _graph;

    /// The classes worth deserializing to find a renderer. Everything else in a bundle — the
    /// animation, the particle systems, the components — cannot lead from a mesh to a texture.
    private static readonly HashSet<int> Drawing =
    [
        (int)AssetClassID.MeshFilter, (int)AssetClassID.MeshRenderer,
        (int)AssetClassID.SkinnedMeshRenderer,
    ];

    /// What the renderers put on one mesh, in submesh order.
    ///
    /// Scanned rather than looked up, because nothing points from a mesh to the renderer using it.
    /// Only the three classes that can draw are read, which on the largest bundle in the game is a
    /// fraction of a second — and the answer is worth that once, when somebody opens a model.
    ///
    /// The renderer is often not where the mesh is. A weapon's prefab and its geometry can sit in
    /// different bundles, and then the mesh's own bundle holds nobody who draws it — which is why
    /// this takes somewhere to look rather than assuming. The mesh is identified by bundle and id
    /// together, so a renderer pointing at the same id in a different file is not mistaken for one
    /// pointing at this mesh.
    /// <param name="lookIn">
    /// Bundles that might hold a renderer. The mesh's own is tried first and is enough for most
    /// things; the rest are for a model whose prefab lives elsewhere.
    /// </param>
    public IReadOnlyList<AssetNode?> For(string meshBundle, long meshPathId, IEnumerable<string>? lookIn = null)
    {
        var searching = new[] { meshBundle }
            .Concat(lookIn ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var bundle in searching)
            if (In(bundle, meshBundle, meshPathId) is { Count: > 0 } found)
                return found;

        return [];
    }

    private IReadOnlyList<AssetNode?> In(string bundle, string meshBundle, long meshPathId)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return []; }

        var meshOfGameObject = new Dictionary<long, bool>();
        var renderers = new List<(long GameObject, bool Mesh, AssetTypeValueField Field)>();

        foreach (var info in file.file.AssetInfos)
        {
            if (!Drawing.Contains(info.TypeId)) continue;

            var field = bundles.Context.Deserialize(file, info);
            if (field is null) continue;

            var owner = field["m_GameObject"];
            var on = owner.IsDummy ? 0 : owner["m_PathID"].AsLong;

            // A MeshFilter says which mesh its GameObject holds; the MeshRenderer beside it says
            // what to paint it with and never names the mesh itself. A SkinnedMeshRenderer is both
            // at once.
            if (info.TypeId == (int)AssetClassID.MeshFilter)
                meshOfGameObject[on] = Names(file, bundle, field["m_Mesh"], meshBundle, meshPathId);
            else
                renderers.Add((on, info.TypeId == (int)AssetClassID.SkinnedMeshRenderer
                    && Names(file, bundle, field["m_Mesh"], meshBundle, meshPathId), field));
        }

        foreach (var (gameObject, named, renderer) in renderers)
        {
            if (!named && !meshOfGameObject.GetValueOrDefault(gameObject)) continue;

            var slots = renderer["m_Materials"]["Array"].Children
                .Select(m => MainTextureOf(bundle, m["m_FileID"].AsInt, m["m_PathID"].AsLong))
                .ToList();

            if (slots.Any(s => s is not null)) return slots;
        }

        return [];
    }

    /// Whether a pointer names this exact mesh, following it out of the file if it leaves.
    private bool Names(
        AssetsFileInstance from, string bundle, AssetTypeValueField pointer,
        string meshBundle, long meshPathId)
    {
        if (pointer.IsDummy || pointer["m_PathID"].AsLong != meshPathId) return false;

        var fileId = pointer["m_FileID"].AsInt;
        var lives = fileId == 0
            ? bundle
            : _graph.Resolve(from, fileId) is { } other ? other.Bundle : "";

        return string.Equals(lives, meshBundle, StringComparison.OrdinalIgnoreCase);
    }

    /// The texture bound to a material's main slot, wherever the material and the texture live.
    public AssetNode? MainTextureOf(string from, int fileId, long pathId)
    {
        if (pathId == 0) return null;

        var (file, bundle) = fileId == 0
            ? (SafeOpen(from), from)
            : SafeOpen(from) is { } origin && _graph.Resolve(origin, fileId) is { } next
                ? (next.File, next.Bundle)
                : (null, "");

        if (file is null) return null;

        var info = file.file.GetAssetInfo(pathId);
        var material = info is null ? null : bundles.Context.Deserialize(file, info);
        return material is null ? null : MainTextureIn(file, bundle, material);
    }

    /// The texture a material binds to its main slot, wherever that texture lives.
    public AssetNode? MainTextureIn(AssetsFileInstance file, string bundle, AssetTypeValueField material)
    {
        // _MainTex first; some materials only bind another slot, and showing that beats showing
        // nothing at all.
        var slots = material["m_SavedProperties"]["m_TexEnvs"]["Array"].Children;
        var main = slots.FirstOrDefault(s => s["first"].AsString == "_MainTex") ?? slots.FirstOrDefault();
        if (main is null) return null;

        var pointer = main["second"]["m_Texture"];
        var textureId = pointer["m_PathID"].AsLong;
        if (textureId == 0) return null;

        var (textureFile, textureBundle) = pointer["m_FileID"].AsInt == 0
            ? (file, bundle)
            : _graph.Resolve(file, pointer["m_FileID"].AsInt) is { } other ? (other.File, other.Bundle) : (null, "");
        if (textureFile is null) return null;

        var textureInfo = textureFile.file.GetAssetInfo(textureId);
        if (textureInfo is null || textureInfo.TypeId != (int)AssetClassID.Texture2D) return null;

        var name = bundles.Context.Deserialize(textureFile, textureInfo)?["m_Name"].AsString ?? "";
        return new AssetNode(textureId, AssetClassID.Texture2D, name, textureBundle);
    }

    private AssetsFileInstance? SafeOpen(string bundle)
    {
        try { return bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }
    }
}
