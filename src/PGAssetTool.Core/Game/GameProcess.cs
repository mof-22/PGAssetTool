using System.Diagnostics;

namespace PGAssetTool.Core.Game;

/// Finding, closing and starting the game.
///
/// Writing to a bundle the game has open leaves a half-written file behind — the failure mode that
/// makes people distrust modding tools — so an apply always asks whether it is running, whatever
/// the settings say about doing anything about it.
public static class GameProcess
{
    public const string AppIdFileName = "steam_appid.txt";

    /// The running game, or null. Matched on where the executable actually is rather than on its
    /// name, so a second installation elsewhere is not mistaken for this one.
    public static Process? Find(GameInstallation game)
    {
        var root = Path.GetFullPath(game.RootDirectory).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception)
            {
                // Most processes cannot be queried at all from a normal user account, and the ones
                // that refuse are not the game.
            }

            if (path is not null
                && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return process;

            process.Dispose();
        }

        return null;
    }

    public static bool IsRunning(GameInstallation game)
    {
        using var found = Find(game);
        return found is not null;
    }

    /// Ends the game and waits for it to go.
    ///
    /// Killed rather than asked politely: this game keeps its state in the cloud and closes on its
    /// own schedule, and a request it decides to ignore leaves the caller no better off. Never the
    /// default, though — someone else's session is not this tool's to end.
    public static bool Close(GameInstallation game, TimeSpan timeout)
    {
        using var found = Find(game);
        if (found is null) return true;

        try
        {
            found.Kill(entireProcessTree: true);
            return found.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to end.
            return !IsRunning(game);
        }
    }

    /// Starts the game, through Steam unless Steam is already up.
    ///
    /// The executable refuses to run without Steam behind it, but going through the exe directly
    /// when Steam is already there skips the handoff and starts noticeably sooner.
    public static bool Launch(GameInstallation game)
    {
        var exe = Directory.EnumerateFiles(game.RootDirectory, "*.exe")
            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("Unity", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (exe is not null && SteamIsRunning())
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = game.RootDirectory });
                return true;
            }

            if (AppId(game) is not { } id) return false;
            Process.Start(new ProcessStartInfo($"steam://rungameid/{id}") { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// Read from the installation rather than hardcoded, so a regional or renamed build still works.
    public static string? AppId(GameInstallation game)
    {
        var path = Path.Combine(game.RootDirectory, AppIdFileName);
        if (!File.Exists(path)) return null;

        var id = File.ReadAllText(path).Trim();
        return id.Length > 0 && id.All(char.IsDigit) ? id : null;
    }

    private static bool SteamIsRunning()
    {
        var found = Process.GetProcessesByName("steam");
        foreach (var process in found) process.Dispose();
        return found.Length > 0;
    }
}
