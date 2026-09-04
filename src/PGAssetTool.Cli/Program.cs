using System.Diagnostics;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Weapons;

var command = args.FirstOrDefault() ?? "help";
var positional = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToArray();
string? Option(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault();

if (command is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        pgassettool <command> [options]

          info                 Show the detected installation and version.
          weapons [<filter>]   List weapons, optionally filtered by slug, tag or prefab name.
          show <weapon>        Show one weapon and everything it references.

        Options:
          --game <directory>   Use this installation instead of the detected one.
          --language <bundle>  Localization bundle to read names from (default l_en-gb).
        """);
    return 0;
}

GameInstallation game;
try
{
    var dir = Option("game");
    game = dir is null ? GameInstallation.OpenDetected() : GameInstallation.Open(dir);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

using var bundles = new BundleSet(game);

if (command == "info")
{
    Console.WriteLine($"Install    {game.RootDirectory}");
    Console.WriteLine($"Bundles    {bundles.BundleNames.Count}");
    Console.WriteLine($"Data files {game.EnumerateSerializedFiles().Count()}");
    Console.WriteLine($"Version    {(bundles.Context.HasClassDatabase
        ? GameVersion.Read(bundles.Context, game)
        : $"unavailable ({ClassPackage.FileName} not found)")}");
    return 0;
}

if (command is not ("weapons" or "show"))
{
    Console.Error.WriteLine($"Unknown command '{command}'. Try --help.");
    return 2;
}

var timer = Stopwatch.StartNew();
var catalogs = GameCatalogs.Load(bundles, Option("language") ?? GameCatalogs.DefaultLanguage);
var catalogTime = timer.ElapsedMilliseconds;

if (command == "weapons")
{
    var filter = positional.FirstOrDefault();
    var matches = (filter is null ? catalogs.Items.Weapons : catalogs.Items.Search(filter)).ToList();
    foreach (var w in matches)
        Console.WriteLine($"{w.WeaponNumber,5}  {catalogs.Localization.Translate(w.LocalizationKey) ?? w.Slug,-34} {w.Slug,-34} {w.Tag}");
    Console.Error.WriteLine($"\n{matches.Count} of {catalogs.Items.Count} weapons  (catalogs {catalogTime}ms)");
    return 0;
}

var query = positional.FirstOrDefault();
if (query is null)
{
    Console.Error.WriteLine("show requires a weapon number, prefab name or slug.");
    return 2;
}

var record = catalogs.Items.Find(query);
if (record is null)
{
    Console.Error.WriteLine($"No weapon matches '{query}'.");
    return 1;
}

var tree = new WeaponResolver(bundles, catalogs).Resolve(record);

Console.WriteLine($"{tree.DisplayName}");
Console.WriteLine($"  index {record.Index}  prefab {record.PrefabName}  slug {record.Slug}  tag {record.Tag}");
Console.WriteLine();
Console.WriteLine($"  Prefab   Weapons/{record.PrefabName} @ {tree.PrefabBundle ?? "?"}");
foreach (var group in tree.PrefabAssets.GroupBy(a => a.Class).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"    {group.Key} ({group.Count()})");
    foreach (var node in group.Where(n => n.Name.Length > 0).Take(8))
        Console.WriteLine($"      {node.Name}");
}

Console.WriteLine();
Console.WriteLine($"  Skins ({tree.Skins.Count})");
foreach (var skin in tree.Skins)
{
    Console.WriteLine($"    {skin.Record.Id}  {(skin.DisplayName is null ? "" : $"\"{skin.DisplayName}\"")}");
    foreach (var path in skin.Record.MaterialPaths)
    {
        var hit = catalogs.Lookup.Resolve(path, AssetLookup.SkinAssetRoots);
        Console.WriteLine(hit is null
            ? $"      material  {path} @ ?"
            : $"      material  {hit.Value.Path} @ {hit.Value.Bundle}");
    }
}

if (tree.UnresolvedReasons.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  Unresolved");
    foreach (var reason in tree.UnresolvedReasons) Console.WriteLine($"    {reason}");
}

Console.Error.WriteLine($"\ncatalogs {catalogTime}ms, total {timer.ElapsedMilliseconds}ms");
return 0;
