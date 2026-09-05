using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// Which prefabs in a bundle end up using a given object.
///
/// Textures, meshes and sounds are shared. Two weapons can be built on one model, a skin can be a
/// recolour of a texture the base weapon also uses, and Unity's built-in particle material is used
/// by dozens of things at once. Replacing any of those changes everything that reaches it, which is
/// worth saying out loud before it is written to the game.
///
/// The answer is the prefab root rather than the object holding the renderer, because that is the
/// name a player would recognise — 'Weapon119', not the GameObject called 'Plasma_Pistol_Mesh' that
/// several weapons each have one of.
public sealed class BundleUsage
{
    /// Everything that can lead from a payload back to a prefab. Deserializing the rest — animation
    /// clips, particle systems, the physics components — costs six times as much and adds nothing:
    /// 2.4 seconds against 0.4 on the largest bundle in the game.
    private static readonly HashSet<int> Carriers =
    [
        (int)AssetClassID.Material, (int)AssetClassID.MeshFilter, (int)AssetClassID.MeshRenderer,
        (int)AssetClassID.SkinnedMeshRenderer, (int)AssetClassID.ParticleSystemRenderer,
        (int)AssetClassID.GameObject, (int)AssetClassID.Transform, (int)AssetClassID.RectTransform,
        (int)AssetClassID.MonoBehaviour,
    ];

    private readonly Dictionary<long, List<long>> _referrers = [];
    private readonly Dictionary<long, string> _names = [];
    private readonly Dictionary<long, long> _ownerOf = [];      // component -> GameObject
    private readonly Dictionary<long, long> _fatherOf = [];     // Transform -> parent Transform
    private readonly Dictionary<long, long> _transformOf = [];  // GameObject -> its Transform

    private BundleUsage() { }

    public static BundleUsage Build(AssetsContext context, AssetsFileInstance file)
    {
        var usage = new BundleUsage();

        foreach (var info in file.file.AssetInfos)
        {
            if (!Carriers.Contains(info.TypeId)) continue;

            var field = context.Deserialize(file, info);
            if (field is null) continue;

            var name = field["m_Name"];
            if (!name.IsDummy) usage._names[info.PathId] = name.AsString;

            foreach (var pointer in ReferenceWalker.Pointers(field).Where(p => p.IsLocal).Distinct())
                (usage._referrers.TryGetValue(pointer.PathId, out var list)
                    ? list
                    : usage._referrers[pointer.PathId] = []).Add(info.PathId);

            var owner = field["m_GameObject"];
            var owned = !owner.IsDummy && owner["m_FileID"].AsInt == 0;
            if (owned) usage._ownerOf[info.PathId] = owner["m_PathID"].AsLong;

            if (info.TypeId is (int)AssetClassID.Transform or (int)AssetClassID.RectTransform)
            {
                var father = field["m_Father"];
                if (!father.IsDummy && father["m_PathID"].AsLong != 0)
                    usage._fatherOf[info.PathId] = father["m_PathID"].AsLong;
                if (owned) usage._transformOf[owner["m_PathID"].AsLong] = info.PathId;
            }
        }

        return usage;
    }

    /// The prefabs that reach this object, by name, in order.
    public IReadOnlyList<string> PrefabsUsing(long pathId)
    {
        var seen = new HashSet<long> { pathId };
        var queue = new Queue<long>([pathId]);
        var prefabs = new SortedSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            foreach (var referrer in _referrers.GetValueOrDefault(queue.Dequeue()) ?? [])
            {
                if (!seen.Add(referrer)) continue;

                // A component answers with the prefab it hangs off; anything else is a step on the
                // way there, most often a material between a texture and the renderer drawing it.
                if (_ownerOf.TryGetValue(referrer, out var gameObject)) prefabs.Add(RootOf(gameObject));
                else queue.Enqueue(referrer);
            }
        }

        return [.. prefabs];
    }

    private string RootOf(long gameObject)
    {
        var transform = _transformOf.GetValueOrDefault(gameObject);

        // A malformed hierarchy could loop; the depth cap costs nothing and cannot hang the tool.
        for (var hops = 0; transform != 0 && hops < 64; hops++)
        {
            if (!_fatherOf.TryGetValue(transform, out var father)) break;
            transform = father;
        }

        var root = transform != 0 ? _ownerOf.GetValueOrDefault(transform, gameObject) : gameObject;
        return _names.GetValueOrDefault(root, _names.GetValueOrDefault(gameObject, "?"));
    }
}
