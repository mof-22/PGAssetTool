using System.Diagnostics;
using System.Text;
using PGAssetTool.Cli;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Weapons;

// Names come from twelve localization bundles; the Windows console defaults to a legacy code page
// that mangles all of them.
try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

var command = args.FirstOrDefault() ?? "help";
var positional = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToArray();
string? Option(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault();

if (command is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        pgassettool <command> [options]

          info                 Show the detected installation and version.
          weapons [<filter>]   List weapons, optionally filtered by name, slug, tag or prefab.
          show <weapon>        Show one weapon and everything it references. Takes the in-game
                               number (819), a prefab name (Weapon1257) or a slug. Note that the
                               in-game number and the prefab number are different sequences.

          extract <weapon>     Write out everything belonging to a weapon: images as PNG, audio as
                               WAV, the object graph as JSON.

        Options:
          --game <directory>   Use this installation instead of the detected one.
          --language <bundle>  Localization bundle to read names from (default l_en-gb).
          --out <directory>    Where extract writes (default ./workspace).
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

if (command is not ("weapons" or "show" or "extract"))
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
    var matches = (filter is null ? catalogs.Items.Weapons : catalogs.Items.Search(filter, catalogs.Localization)).ToList();
    foreach (var w in matches)
        Console.WriteLine($"{w.GameNumber,5}  "
            + $"{TextColumn.Pad(catalogs.Localization.Translate(w.LocalizationKey) ?? w.Slug, 34)} "
            + $"{TextColumn.Pad(w.Slug, 34)} {TextColumn.Pad(w.PrefabName, 12)}"
            + $"{(w.IsHidden ? "  (hidden)" : "")}");
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

if (command == "extract")
{
    var outputRoot = Option("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "workspace");
    var export = new WeaponExporter(bundles, catalogs).Export(tree, outputRoot);

    Console.WriteLine($"#{record.GameNumber}  {tree.DisplayName}");
    Console.WriteLine($"  -> {export.Directory}");
    foreach (var group in export.Assets.GroupBy(a => Path.GetDirectoryName(a.Path)).OrderBy(g => g.Key))
    {
        var folder = Path.GetRelativePath(export.Directory, group.Key!).Replace('\\', '/');
        Console.WriteLine($"    {folder}/  ({group.Count()})");
        foreach (var asset in group.OrderBy(a => a.Path))
            Console.WriteLine($"      {TextColumn.Pad(Path.GetFileName(asset.Path), 52)} {asset.Bytes,10:N0}");
    }
    if (export.Skipped.Count > 0)
    {
        Console.WriteLine($"\n  Skipped ({export.Skipped.Count})");
        foreach (var reason in export.Skipped.Take(10)) Console.WriteLine($"    {reason}");
    }
    Console.Error.WriteLine($"\n{export.Assets.Count} files, total {timer.ElapsedMilliseconds}ms");
    return 0;
}

Console.WriteLine($"#{record.GameNumber}  {tree.DisplayName}{(record.IsHidden ? "   (hidden: no localization key, absent from the in-game list)" : "")}");
Console.WriteLine($"  prefab {record.PrefabName}  index {record.Index}  slug {record.Slug}  tag {record.Tag}");
Console.WriteLine();
if (tree.Icon is not null)
    Console.WriteLine($"  Icon     {tree.Icon.TextureName} @ {tree.Icon.Container}");
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

if (tree.Related.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  Related ({tree.Related.Count})");
    foreach (var group in tree.Related.GroupBy(r => r.Namespace))
    {
        Console.WriteLine($"    {group.Key} ({group.Count()})");
        foreach (var asset in group)
            Console.WriteLine($"      {TextColumn.Pad(asset.Path, 66)} @ {asset.Bundle ?? "?"}");
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
