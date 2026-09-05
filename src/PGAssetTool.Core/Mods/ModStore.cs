using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Mods;

public sealed record InstalledMod
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public required string PackPath { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public required string GameVersion { get; init; }
    public bool Enabled { get; init; } = true;

    /// The bundles this mod wrote to, and the manifest hash each had when it did. A bundle whose
    /// hash has since changed was replaced by a game update, which is what makes reapplying and
    /// backup cleanup possible.
    public required Dictionary<string, string> TouchedBundles { get; init; }
}

/// The tool's own directory: original bundles it has replaced, and what is installed.
///
/// Deliberately outside the game folder. Uninstalling the game removes that folder, and the
/// downloaded bundle cache in the player's profile survives it — so backups kept beside the game
/// would be destroyed exactly when a modified bundle outlived them. Each installation gets its own
/// subdirectory, named after its path, so several do not share a store.
public sealed class ModStore
{
    public const string DirectoryName = "PGAssetTool";
    private const string StateFileName = "installed.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GameInstallation _game;

    public ModStore(GameInstallation game)
    {
        _game = game;
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DirectoryName, "installs", KeyFor(game.RootDirectory));
        BackupRoot = Path.Combine(Root, "backup");
    }

    /// Readable enough to recognise, with a digest of the full path so two installations sharing a
    /// folder name do not share a store.
    private static string KeyFor(string installPath)
    {
        var full = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];
        var name = Path.GetFileName(full);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return $"{name}-{digest}";
    }

    public string Root { get; }
    public string BackupRoot { get; }
    private string StatePath => Path.Combine(Root, StateFileName);

    public List<InstalledMod> Read()
        => File.Exists(StatePath)
            ? JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(StatePath), Json) ?? []
            : [];

    public void Write(IEnumerable<InstalledMod> mods)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(mods.ToList(), Json));
    }

    /// Backups are filed under the cache they came from and the bundle's hash. The cache matters
    /// because the same bundle exists in two of them; the hash keeps a backup taken before an update
    /// distinguishable from the version that replaced it.
    public string BackupPathFor(CacheKind cache, string bundle, string hash)
        => Path.Combine(BackupRoot, cache.ToString().ToLowerInvariant(), bundle, hash, bundle);

    public bool HasBackup(CacheKind cache, string bundle, string hash)
        => File.Exists(BackupPathFor(cache, bundle, hash));

    public void Backup(CacheKind cache, string bundle, string hash, string livePath)
    {
        var destination = BackupPathFor(cache, bundle, hash);
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(livePath, destination);
    }

    public bool RestoreIfBackedUp(CacheKind cache, string bundle, string hash, string livePath)
    {
        var source = BackupPathFor(cache, bundle, hash);
        if (!File.Exists(source)) return false;
        File.Copy(source, livePath, overwrite: true);
        return true;
    }

    /// A game update replaces a bundle and gives it a new hash directory. The modified copy under
    /// the old hash and the backup beside it are both dead weight at that point.
    public IReadOnlyList<string> PruneStaleBackups(IReadOnlyDictionary<string, string> currentHashes)
    {
        if (!Directory.Exists(BackupRoot)) return [];

        var removed = new List<string>();
        foreach (var cacheDirectory in Directory.GetDirectories(BackupRoot))
        {
            foreach (var bundleDirectory in Directory.GetDirectories(cacheDirectory))
            {
                var bundle = Path.GetFileName(bundleDirectory);
                currentHashes.TryGetValue(bundle, out var currentHash);
                foreach (var hashDirectory in Directory.GetDirectories(bundleDirectory))
                {
                    if (Path.GetFileName(hashDirectory) == currentHash) continue;
                    Directory.Delete(hashDirectory, recursive: true);
                    removed.Add($"{Path.GetFileName(cacheDirectory)}/{bundle}/{Path.GetFileName(hashDirectory)}");
                }
                if (Directory.GetFileSystemEntries(bundleDirectory).Length == 0)
                    Directory.Delete(bundleDirectory);
            }
            if (Directory.GetFileSystemEntries(cacheDirectory).Length == 0)
                Directory.Delete(cacheDirectory);
        }
        return removed;
    }
}
