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
    public const string DataDirectoryName = "PGAssetTool-data";
    private const string StateFileName = "installed.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GameInstallation _game;

    /// Where everything this tool keeps lives: beside the executable, not in the user profile.
    public string Home { get; }

    public ModStore(GameInstallation game, string? home = null)
    {
        _game = game;
        Home = home ?? DefaultHome();
        Root = Path.Combine(Home, "installs", KeyFor(game.RootDirectory));
        BackupRoot = Path.Combine(Root, "backup");
    }

    /// Beside the tool, not beside the game and not off in the user profile.
    ///
    /// From a published build that is the executable's own directory. From a development build the
    /// executable sits several levels down in bin/, which a rebuild can wipe, so the search walks up
    /// to the checkout it belongs to and settles there instead.
    public static string DefaultHome()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var candidate = directory; candidate is not null; candidate = candidate.Parent)
        {
            if (candidate.EnumerateDirectories(".git").Any() || candidate.EnumerateFiles("*.slnx").Any())
                return Path.Combine(candidate.FullName, DataDirectoryName);
        }
        return Path.Combine(directory.FullName, DataDirectoryName);
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

    /// Installed packs, copied here so the ledger never points at wherever an author happened to
    /// build one. Deleting a workspace used to leave an installed mod with no file to reapply from.
    public string ModsDirectory => Path.Combine(Home, "mods");
    public string BackupRoot { get; }
    private string StatePath => Path.Combine(Root, StateFileName);

    public List<InstalledMod> Read()
        => File.Exists(StatePath)
            ? JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(StatePath), Json) ?? []
            : [];

    /// Keeps the previous contents beside the file before overwriting.
    ///
    /// Losing this file does not lose the backups — those are enumerable on their own — but it does
    /// lose the record of which mod put what where, and with it the ability to uninstall through the
    /// tool rather than by hand.
    public void Write(IEnumerable<InstalledMod> mods)
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(StatePath)) File.Copy(StatePath, StatePath + ".previous", overwrite: true);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(mods.ToList(), Json));
    }

    /// Backups are filed under the bundle's hash, so a backup taken before an update stays
    /// distinguishable from the version that replaced it.
    public string BackupPathFor(CacheKind cache, string bundle, string hash)
        => Path.Combine(BackupRoot, cache.ToString().ToLowerInvariant(), bundle, hash, bundle);

    /// Every bundle this store holds a backup of — the record of what has actually been written to.
    ///
    /// The installed list cannot answer that. Uninstalling drops the mod before its bundles are put
    /// back, so a bundle whose only mod is gone is still modified and no longer named anywhere else.
    public IEnumerable<(CacheKind Cache, string Bundle, string Hash)> BackedUp()
    {
        if (!Directory.Exists(BackupRoot)) yield break;

        foreach (var cacheDirectory in Directory.GetDirectories(BackupRoot))
        {
            // An older layout filed backups directly under the bundle name, with no cache segment.
            if (!Enum.TryParse<CacheKind>(Path.GetFileName(cacheDirectory), ignoreCase: true, out var cache))
                continue;

            foreach (var bundleDirectory in Directory.GetDirectories(cacheDirectory))
            {
                var bundle = Path.GetFileName(bundleDirectory);
                foreach (var hashDirectory in Directory.GetDirectories(bundleDirectory))
                    if (File.Exists(Path.Combine(hashDirectory, bundle)))
                        yield return (cache, bundle, Path.GetFileName(hashDirectory));
            }
        }
    }

    /// Takes a copy of a pack so installing it does not depend on the file staying where it was.
    ///
    /// A pack built into a workspace is one deletion away from leaving an installed mod that cannot
    /// be reapplied or removed cleanly — which happened, and cost an uninstall to recover from.
    public string Keep(string packPath)
    {
        Directory.CreateDirectory(ModsDirectory);

        // Already here: reinstalling from the copy must not spiral into copies of copies.
        var full = Path.GetFullPath(packPath);
        if (full.StartsWith(Path.GetFullPath(ModsDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return full;

        var kept = KeptPathFor(full);
        File.Copy(packPath, kept, overwrite: true);
        return kept;
    }

    /// Throws away the copy taken of a mod's pack, once nothing is installed from it.
    ///
    /// Only ever a file of this store's own: a mod installed from a pack that was already in the
    /// mods directory names that file, but one installed from a workspace names the copy, and the
    /// author's original is theirs. Anything outside the directory is left alone and reported as
    /// not deleted rather than quietly passed over, because "removed the file" and "the file was
    /// somewhere I do not touch" are different answers to the same request.
    ///
    /// Answers whether a file went. Nothing to delete is not a failure — a mod whose pack has
    /// already been deleted by hand is exactly the case this is being used to reach.
    public bool DiscardKeptPack(InstalledMod mod)
    {
        if (mod.PackPath.Length == 0) return false;

        var full = Path.GetFullPath(mod.PackPath);
        if (!full.StartsWith(Path.GetFullPath(ModsDirectory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(full)) return false;
        File.Delete(full);
        return true;
    }

    /// The name a pack built at `sourcePath` is filed under.
    ///
    /// The digest goes after the name rather than in front of it: the folder is browsed by a person
    /// looking for a mod they built, and eight hex characters at the start of every row means
    /// sorting by name sorts by nothing and reading the list means reading past the same noise each
    /// time. It still has to be there — two packs called the same thing from different directories
    /// would otherwise be one file — but it belongs where a disambiguator belongs.
    private string KeptPathFor(string sourcePath)
    {
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..8];
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(ModsDirectory, $"{name}-{digest}{Path.GetExtension(sourcePath)}");
    }

    /// Moves packs filed under the old digest-first name, and points the ledger at where they went.
    ///
    /// Renaming without the second half would leave every installed mod naming a file that is no
    /// longer there, and with it no way to reapply or cleanly remove one.
    public IReadOnlyList<string> TidyKeptPackNames()
    {
        if (!Directory.Exists(ModsDirectory)) return [];

        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(ModsDirectory, "*" + Pack.PackBuilder.Extension))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length < 10 || name[8] != '-') continue;
            var digest = name[..8];
            if (!digest.All(char.IsAsciiHexDigitLower)) continue;

            var renamed = Path.Combine(
                ModsDirectory, $"{name[9..]}-{digest}{Path.GetExtension(path)}");
            if (File.Exists(renamed)) continue;

            File.Move(path, renamed);
            moved[Path.GetFullPath(path)] = renamed;
        }

        if (moved.Count == 0) return [];

        string? Where(string path) => path.Length == 0 ? null : Path.GetFullPath(path);

        var installed = Read();
        if (installed.Exists(m => Where(m.PackPath) is { } at && moved.ContainsKey(at)))
            Write(installed.Select(m => Where(m.PackPath) is { } at && moved.TryGetValue(at, out var to)
                ? m with { PackPath = to }
                : m));

        return moved.Values.Select(Path.GetFileName).ToList()!;
    }

    public bool HasBackup(CacheKind cache, string bundle, string hash) => File.Exists(BackupPathFor(cache, bundle, hash));

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
    ///
    /// Which hash counts as current depends on the cache: the shipped copy follows the manifest, the
    /// downloaded one follows its own ledger, and the two can disagree.
    public IReadOnlyList<string> PruneStaleBackups(IReadOnlyDictionary<string, string> manifestHashes)
    {
        if (!Directory.Exists(BackupRoot)) return [];

        var removed = new List<string>();
        foreach (var cacheDirectory in Directory.GetDirectories(BackupRoot))
        {
            var cacheName = Path.GetFileName(cacheDirectory);
            var downloaded = string.Equals(cacheName, nameof(CacheKind.Downloaded),
                StringComparison.OrdinalIgnoreCase);

            foreach (var bundleDirectory in Directory.GetDirectories(cacheDirectory))
            {
                var bundle = Path.GetFileName(bundleDirectory);
                var current = downloaded
                    ? _game.Downloaded?.Claimed.GetValueOrDefault(bundle)
                    : manifestHashes.GetValueOrDefault(bundle);

                foreach (var hashDirectory in Directory.GetDirectories(bundleDirectory))
                {
                    if (Path.GetFileName(hashDirectory) == current) continue;
                    Directory.Delete(hashDirectory, recursive: true);
                    removed.Add($"{cacheName}/{bundle}/{Path.GetFileName(hashDirectory)}");
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
