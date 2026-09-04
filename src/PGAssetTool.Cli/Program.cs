using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Game;

var command = args.FirstOrDefault() ?? "info";
var gameDir = args.SkipWhile(a => a != "--game").Skip(1).FirstOrDefault();

if (command is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        pgassettool <command> [--game <directory>]

          info    Show the detected installation, version and asset file counts.
        """);
    return 0;
}

if (command != "info")
{
    Console.Error.WriteLine($"Unknown command '{command}'. Try --help.");
    return 2;
}

GameInstallation game;
try
{
    game = gameDir is null ? GameInstallation.OpenDetected() : GameInstallation.Open(gameDir);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine($"Install    {game.RootDirectory}");
Console.WriteLine($"Data       {game.DataDirectory}");

var manifest = game.ReadManifest();
var present = manifest.Count(e => File.Exists(e.PathUnder(game.BundlesDirectory)));
Console.WriteLine($"Bundles    {manifest.Count} in manifest, {present} present on disk");

var serialized = game.EnumerateSerializedFiles().ToList();
Console.WriteLine($"Data files {serialized.Count} serialized files outside the bundle cache");

using var context = new AssetsContext();
if (context.HasClassDatabase)
    Console.WriteLine($"Version    {GameVersion.Read(context, game)}");
else
    Console.WriteLine($"Version    unavailable ({ClassPackage.FileName} not found)");

return 0;
