using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Assets;

/// Name-addressed access to the bundle cache. Resolves a bundle's hash directory through the
/// manifest so callers work in bundle names rather than paths.
public sealed class BundleSet : IDisposable
{
    private readonly AssetsContext _context;
    private readonly Dictionary<string, string> _hashes;

    public GameInstallation Game { get; }

    public BundleSet(GameInstallation game, AssetsContext? context = null)
    {
        Game = game;
        _context = context ?? new AssetsContext();
        _hashes = game.ReadManifest().ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase);
    }

    public AssetsContext Context => _context;
    public IReadOnlyCollection<string> BundleNames => _hashes.Keys;

    public string HashOf(string bundle) => _hashes.TryGetValue(bundle, out var hash)
        ? hash
        : throw new KeyNotFoundException($"'{bundle}' is not in the bundle manifest.");

    /// The copy the game would load, which is not always the one shipped with it.
    public string PathOf(string bundle)
        => Game.Locate(bundle, HashOf(bundle))?.Path
           ?? throw new FileNotFoundException($"No copy of '{bundle}' is present in any cache.");

    /// The first serialized file in a bundle. Every content bundle here holds exactly one.
    public AssetsFileInstance Open(string bundle)
    {
        var file = _context.OpenBundle(PathOf(bundle));
        var entry = AssetsContext.SerializedEntries(file).First();
        return _context.OpenBundleEntry(file, entry.Index);
    }

    public AssetTypeValueField MonoBehaviour(string bundle, string name)
        => TryMonoBehaviour(bundle, name)
           ?? throw new KeyNotFoundException($"No MonoBehaviour named '{name}' in bundle '{bundle}'.");

    public AssetTypeValueField? TryMonoBehaviour(string bundle, string name)
    {
        var file = Open(bundle);
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetClassID.MonoBehaviour) continue;
            var field = _context.Deserialize(file, info);
            if (field is not null && (name == "*" || field["m_Name"].AsString == name)) return field;
        }
        return null;
    }

    /// Texture and audio payloads are not stored in the serialized object; they sit in a sibling
    /// .resS or .resource entry that the object points into. This reads that slice.
    public byte[] ReadResource(string bundle, string sourcePath, long offset, long size)
    {
        var wanted = Path.GetFileName(sourcePath);
        var file = _context.OpenBundle(PathOf(bundle));
        foreach (var entry in file.file.BlockAndDirInfo.DirectoryInfos)
        {
            if (!string.Equals(entry.Name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var reader = file.file.DataReader;
            reader.Position = entry.Offset + offset;
            return reader.ReadBytes((int)size);
        }
        throw new FileNotFoundException($"Bundle '{bundle}' has no stream entry '{wanted}'.");
    }

    public void Dispose() => _context.Dispose();
}
