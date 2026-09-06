using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// Where an asset lives, written so it survives a game update.
///
/// `PathId` is the fast path and is usually still correct, but it does move: comparing one update
/// against the next, 142 objects across 8 bundles changed theirs. So it is a hint to verify against
/// the name, not the identity itself. `Ordinal` separates same-named assets in one container,
/// counted within a class and ordered by path id.
public sealed record AssetAddress(
    string Container,
    string Class,
    string Name,
    int Ordinal = 0,
    long? PathId = null)
{
    public override string ToString() => $"{Container}:{Class}:{Name}" + (Ordinal > 0 ? $"#{Ordinal}" : "");
}

/// Resolves between path ids and the name-based address, per serialized file.
public sealed class ContainerIndex(AssetsContext context)
{
    private readonly Dictionary<string, Dictionary<(int Class, string Name), List<long>>> _byContainer = new();

    private Dictionary<(int, string), List<long>> IndexOf(string container, AssetsFileInstance file)
    {
        if (_byContainer.TryGetValue(container, out var cached)) return cached;

        var index = new Dictionary<(int, string), List<long>>();
        foreach (var info in file.file.AssetInfos)
        {
            var key = (info.TypeId, NameOf(file, info));
            if (!index.TryGetValue(key, out var ids)) index[key] = ids = [];
            ids.Add(info.PathId);
        }
        foreach (var ids in index.Values) ids.Sort();
        return _byContainer[container] = index;
    }

    public AssetAddress AddressOf(string container, AssetsFileInstance file, AssetFileInfo info, string name)
    {
        var ids = IndexOf(container, file).GetValueOrDefault((info.TypeId, name)) ?? [];
        var ordinal = ids.IndexOf(info.PathId);
        return new AssetAddress(container, ((AssetClassID)info.TypeId).ToString(), name,
            ordinal < 0 ? 0 : ordinal, info.PathId);
    }

    /// Prefers the recorded path id, but only when the object there still has the expected class and
    /// name. Otherwise falls back to the name, so a pack keeps working after an update moves things.
    public AssetFileInfo? Resolve(AssetAddress address, AssetsFileInstance file, out bool usedPathId)
    {
        usedPathId = false;
        if (!Enum.TryParse<AssetClassID>(address.Class, out var cls)) return null;

        if (address.PathId is { } pathId && file.file.GetAssetInfo(pathId) is { } candidate
            && candidate.TypeId == (int)cls && NameOf(file, candidate) == address.Name)
        {
            usedPathId = true;
            return candidate;
        }

        var ids = IndexOf(address.Container, file).GetValueOrDefault(((int)cls, address.Name));
        if (ids is null || address.Ordinal >= ids.Count) return null;
        return file.file.GetAssetInfo(ids[address.Ordinal]);
    }

    private string NameOf(AssetsFileInstance file, AssetFileInfo info)
        => AssetNaming.NameOf(context.Deserialize(file, info), (AssetClassID)info.TypeId);
}
