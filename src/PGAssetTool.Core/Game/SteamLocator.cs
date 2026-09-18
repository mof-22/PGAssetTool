using Microsoft.Win32;

namespace PGAssetTool.Core.Game;

/// Locates Steam libraries and the installed game directory on Windows.
public static class SteamLocator
{
    public static string? FindSteamRoot()
    {
        foreach (var (hive, key) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
        })
        {
            using var k = hive.OpenSubKey(key);
            var path = k?.GetValue("SteamPath") as string ?? k?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    public static IEnumerable<string> EnumerateLibraryFolders(string steamRoot)
    {
        yield return Path.Combine(steamRoot, "steamapps");

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        // libraryfolders.vdf is Valve KeyValues; every library entry carries a "path" key.
        foreach (var line in File.ReadLines(vdf))
        {
            var t = line.AsSpan().Trim();
            if (!t.StartsWith("\"path\"")) continue;
            int open = line.IndexOf('"', line.IndexOf("\"path\"", StringComparison.Ordinal) + 6);
            if (open < 0) continue;
            int close = line.IndexOf('"', open + 1);
            if (close < 0) continue;
            var path = line[(open + 1)..close].Replace(@"\\", @"\");
            var apps = Path.Combine(path, "steamapps");
            if (Directory.Exists(apps)) yield return apps;
        }
    }

    /// The first installed Steam game laid out the way this tool reads: a `*_Data` directory whose
    /// bundle cache carries the manifest the game keeps of its bundles.
    ///
    /// Recognised by that shape rather than by its folder name, so the tool does not have to carry
    /// the name — and the shape is what GameInstallation.Open would refuse anything without anyway.
    public static string? FindGameDirectory()
    {
        var root = FindSteamRoot();
        if (root is null) return null;

        foreach (var apps in EnumerateLibraryFolders(root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var common = Path.Combine(apps, "common");
            if (!Directory.Exists(common)) continue;

            foreach (var game in Directory.EnumerateDirectories(common).Order(StringComparer.OrdinalIgnoreCase))
                if (IsLaidOutForThis(game)) return game;
        }
        return null;
    }

    public static bool IsLaidOutForThis(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*_Data").Any(data => File.Exists(Path.Combine(
                data, "StreamingAssets", "Cache", "bundles", GameInstallation.ManifestFileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
