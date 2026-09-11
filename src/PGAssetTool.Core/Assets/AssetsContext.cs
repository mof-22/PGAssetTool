using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Assets;

/// Opens the game's serialized files.
///
/// AssetBundles carry their own TypeTree, so they need nothing extra. The files under *_Data
/// (resources.assets, globalgamemanagers, ...) are built without one, so reading them requires
/// Unity's engine class database — that is what classdata.tpk supplies. It describes stock engine
/// classes only and is unrelated to the game's own script types.
public sealed class AssetsContext : IDisposable
{
    private readonly AssetsManager _manager = new();
    private readonly string? _classPackagePath;
    private bool _classDatabaseLoaded;

    public AssetsContext(string? classPackagePath = null)
    {
        _classPackagePath = classPackagePath ?? ClassPackage.Locate();
        if (_classPackagePath is not null)
            _manager.LoadClassPackage(_classPackagePath);
    }

    public bool HasClassDatabase => _classPackagePath is not null;

    public BundleFileInstance OpenBundle(string path) => _manager.LoadBundleFile(path, true);

    public AssetsFileInstance OpenBundleEntry(BundleFileInstance bundle, int index)
        => _manager.LoadAssetsFileFromBundle(bundle, index, false);

    /// Serialized files outside the bundle cache. Requires the class database.
    public AssetsFileInstance OpenSerializedFile(string path)
    {
        if (_classPackagePath is null)
            throw new InvalidOperationException(
                "Reading files under *_Data requires classdata.tpk. Place it next to the executable "
                + "or pass its path explicitly.");

        var inst = _manager.LoadAssetsFile(path, false);
        if (!_classDatabaseLoaded)
        {
            _manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
            _classDatabaseLoaded = true;
        }
        return inst;
    }

    public AssetTypeValueField? Deserialize(AssetsFileInstance file, AssetFileInfo info)
        => _manager.GetBaseField(file, info);

    /// Non-scene entries in a bundle. The .resS/.resource siblings hold raw texture and audio
    /// payloads rather than serialized objects.
    public static IEnumerable<(int Index, string Name)> SerializedEntries(BundleFileInstance bundle)
        => bundle.file.BlockAndDirInfo.DirectoryInfos
            .Select((d, i) => (Index: i, d.Name))
            .Where(e => !e.Name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                     && !e.Name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase));

    /// Puts everything down.
    ///
    /// The library unloads by walking its own list of open bundles, so anything still opening one
    /// on another thread makes that walk throw. Callers are expected not to do that — the window
    /// waits for its readers before disposing — and a shutdown is no place to turn somebody else's
    /// race into a crash on the way out.
    public void Dispose()
    {
        try { _manager.UnloadAll(true); }
        catch (InvalidOperationException) { }
    }
}

public static class ClassPackage
{
    public const string FileName = "classdata.tpk";

    public static string? Locate()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return Unpack();
    }

    /// Writes out the copy carried inside the assembly.
    ///
    /// A single-file build is one executable with no folder to keep the database beside, and asking
    /// people to fetch it separately would mean the newest weapons' icons silently fail to load.
    /// AssetsTools wants a path rather than a stream, so it goes to a fixed place in the temp
    /// directory and is reused from there.
    private static string? Unpack()
    {
        using var embedded = typeof(ClassPackage).Assembly.GetManifestResourceStream(FileName);
        if (embedded is null) return null;

        var path = Path.Combine(Path.GetTempPath(), "PGAssetTool", FileName);
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > 0 } existing && existing.Length == embedded.Length)
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            embedded.CopyTo(file);
            return path;
        }
        catch (IOException)
        {
            // Another process writing the same file, or nowhere to write at all. Reading .assets is
            // optional everywhere it is used, so this degrades rather than fails.
            return File.Exists(path) ? path : null;
        }
    }
}

public static class GameVersion
{
    /// PlayerSettings.bundleVersion, e.g. "26.11.0". The game never downgrades, so a change in this
    /// value is a reliable update signal.
    public static string Read(AssetsContext context, GameInstallation game)
    {
        var ggm = context.OpenSerializedFile(game.GlobalGameManagersPath);
        var info = ggm.file.AssetInfos.First(a => a.TypeId == (int)AssetClassID.PlayerSettings);
        return context.Deserialize(ggm, info)!["bundleVersion"].AsString;
    }
}
