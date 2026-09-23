using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Tests;

/// Which executable in the game's folder is the game.
///
/// The answer decides two things that matter: whether "is the game running" can ever be asked of
/// the right process, and what the launcher starts. Getting it wrong on the first reads as the game
/// being closed, and writing to a bundle the game has open is the failure this tool exists to
/// avoid — so it is worth a test even though it is three lines of path arithmetic.
public class GameProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-process").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameInstallation Installation(string data, params string[] executables)
    {
        var bundles = Path.Combine(_root, data, "StreamingAssets", "Cache", "bundles");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, "embedded_asset_bundles.json"), "[]");
        foreach (var exe in executables) File.WriteAllText(Path.Combine(_root, exe), "MZ");

        return GameInstallation.Open(_root);
    }

    [Fact]
    public void TheGameIsTheExecutableNamedAfterItsDataFolder()
    {
        // As a modded folder looks: the author's own tools in with the game, one of them sorting
        // first, and Unity's crash handler which both rules already knew to skip.
        var game = Installation("Shooter_Data", "AAA Modding Tool.exe", "Shooter.exe", "UnityCrashHandler64.exe");

        Assert.Equal(Path.Combine(_root, "Shooter.exe"), GameProcess.ExecutablePath(game));
    }

    [Fact]
    public void AnInstallationLaidOutSomeOtherWayFallsBackToTheFirstThatIsNotUnitySOwn()
    {
        var game = Installation("Shooter_Data", "UnityCrashHandler64.exe", "Launcher.exe");

        Assert.Equal(Path.Combine(_root, "Launcher.exe"), GameProcess.ExecutablePath(game));
    }

    [Fact]
    public void AFolderWithNothingToStartSaysSo()
        => Assert.Null(GameProcess.ExecutablePath(Installation("Shooter_Data")));
}
