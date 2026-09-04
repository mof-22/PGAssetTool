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

/// The tool's own directory beside the game: original bundles it has replaced, and what is
/// installed. Kept out of the bundle cache so the game never sees it.
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
        Root = Path.Combine(game.RootDirectory, DirectoryName);
        BackupRoot = Path.Combine(Root, "backup");
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

    /// Backups are filed under the bundle's hash, so a backup taken before an update stays
    /// distinguishable from the version that replaced it.
    public string BackupPathFor(string bundle, string hash)
        => Path.Combine(BackupRoot, bundle, hash, bundle);

    public bool HasBackup(string bundle, string hash) => File.Exists(BackupPathFor(bundle, hash));

    public void Backup(string bundle, string hash, string livePath)
    {
        var destination = BackupPathFor(bundle, hash);
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(livePath, destination);
    }

    public bool RestoreIfBackedUp(string bundle, string hash, string livePath)
    {
        var source = BackupPathFor(bundle, hash);
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
        foreach (var bundleDirectory in Directory.GetDirectories(BackupRoot))
        {
            var bundle = Path.GetFileName(bundleDirectory);
            currentHashes.TryGetValue(bundle, out var currentHash);
            foreach (var hashDirectory in Directory.GetDirectories(bundleDirectory))
            {
                if (Path.GetFileName(hashDirectory) == currentHash) continue;
                Directory.Delete(hashDirectory, recursive: true);
                removed.Add($"{bundle}/{Path.GetFileName(hashDirectory)}");
            }
            if (Directory.GetFileSystemEntries(bundleDirectory).Length == 0)
                Directory.Delete(bundleDirectory);
        }
        return removed;
    }
}
