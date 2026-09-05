using System.Diagnostics;
using System.Text;
using PGAssetTool.Cli;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.RawAssets;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
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
                               WAV, the object graph as JSON. With --workspace, also writes a
                               pgmod.json naming every replaceable file.
          pack [<directory>]   Build a .pgmod from a workspace. Only files edited since the
                               extract are included.

          convert <path>       Turn raw .dat assets exported by an asset editor into editable
                               formats, recovering each one's type from the game. The result
                               is a packable workspace.

          apply <pack>         Install a .pgmod into the game.
          verify               Check every bundle against the hash the game recorded for it.
          mods                 List installed mods.
          enable <id>          Turn a mod back on.
          disable <id>         Turn a mod off without uninstalling it.
          remove <id>          Uninstall a mod.

        Options:
          --game <directory>   Use this installation instead of the detected one.
          --language <bundle>  Localization bundle to read names from (default l_en-gb).
          --out <directory>    Where extract writes (default ./workspace).
          --author <name>      Recorded in the manifest by extract --workspace.
          --force              Let apply back up a bundle that is already modified.
        """);
    return 0;
}

// Packing reads a workspace directory and nothing else, so it needs no game installation.
if (command == "pack")
{
    var workspace = positional.FirstOrDefault() ?? Directory.GetCurrentDirectory();
    var output = Option("out")
        ?? Path.Combine(workspace, Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace)) + PackBuilder.Extension);
    try
    {
        var result = PackBuilder.Build(workspace, output);
        Console.WriteLine($"{result.Path}");
        Console.WriteLine($"  {result.Operations} operations, {result.Bytes:N0} bytes");
        foreach (var operation in PackBuilder.ReadManifest(result.Path).Operations)
            Console.WriteLine($"    {operation.Op}  {operation.Target}  <- {operation.Source}");
        if (result.Unchanged.Count > 0)
            Console.WriteLine($"  {result.Unchanged.Count} unedited files left out.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
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
    Console.WriteLine($"Bundles    {bundles.BundleNames.Count} in the manifest");
    Console.WriteLine($"Shipped    {game.BundlesDirectory}");
    if (game.Downloaded is { } dl)
    {
        Console.WriteLine($"Downloaded {dl.Claimed.Count} claimed  {dl.BundlesDirectory}");
        var broken = dl.ClaimedButMissing().ToList();
        if (broken.Count > 0)
            Console.WriteLine($"           {broken.Count} claimed with no file; the game logs a read "
                + "failure for each and falls back to the shipped copy");
    }
    else Console.WriteLine("Downloaded none yet; everything loads from the shipped cache");
    Console.WriteLine($"Data files {game.EnumerateSerializedFiles().Count()}");
    Console.WriteLine($"Version    {(bundles.Context.HasClassDatabase
        ? GameVersion.Read(bundles.Context, game)
        : $"unavailable ({ClassPackage.FileName} not found)")}");
    return 0;
}

if (command == "convert")
{
    var input = positional.FirstOrDefault();
    if (input is null)
    {
        Console.Error.WriteLine("convert requires a .dat file or a directory of them.");
        return 2;
    }

    var sources = RawAssetFile.Discover(input).ToList();
    if (sources.Count == 0)
    {
        Console.Error.WriteLine(
            $"No files in '{input}' are named '<asset>-CAB-<hash>-<pathId>.dat', which is what "
            + "carries the information needed to recover an asset's type.");
        return 1;
    }

    var destination = Option("out") ?? Path.Combine(Path.GetFullPath(input), "converted");
    var converter = new RawAssetConverter(bundles, CabIndex.Build(bundles));
    var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)));

    var manifest = converter.ConvertToWorkspace(
        sources, destination, id,
        author: Option("author") ?? "",
        gameVersion: bundles.Context.HasClassDatabase ? GameVersion.Read(bundles.Context, game) : null,
        onError: (source, ex) => Console.Error.WriteLine($"  {TextColumn.Pad(source.Name, 34)} {ex.Message}"),
        results: out var results);

    foreach (var result in results)
        foreach (var asset in result.Written)
            Console.WriteLine($"  {TextColumn.Pad(result.Source.Name, 34)} {result.Class,-12} "
                + $"@ {TextColumn.Pad(result.Bundle, 12)} -> {Path.GetFileName(asset.Path)}");

    Console.WriteLine($"\n{results.Count} of {sources.Count} converted into {destination}");
    Console.WriteLine($"{PackManifest.FileName} has {manifest.Operations.Count} operation(s).");

    var unusable = results.SelectMany(r => r.Written)
        .Where(a => !manifest.Operations.Any(o => o.Source == Path.GetFileName(a.Path)))
        .Select(a => $"{Path.GetFileName(a.Path)} ({a.Class})")
        .ToList();
    if (unusable.Count > 0)
    {
        Console.WriteLine($"\nLeft out, because nothing can write these back yet:");
        foreach (var name in unusable.Take(8)) Console.WriteLine($"  {name}");
        if (unusable.Count > 8) Console.WriteLine($"  and {unusable.Count - 8} more");
    }
    Console.WriteLine($"\n  pgassettool pack \"{destination}\"");
    return results.Count == sources.Count ? 0 : 1;
}

if (command == "verify")
{
    var store = new ModStore(game);
    var expected = store.Read().SelectMany(m => m.TouchedBundles.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var modified = new List<(string Bundle, bool Known)>();
    var missing = 0;

    var fromDownloaded = 0;
    foreach (var entry in game.ReadManifest())
    {
        if (game.Resolve(entry.Name, entry.Hash) is not { } resolved) { missing++; continue; }
        if (resolved.Cache == CacheKind.Downloaded) fromDownloaded++;
        if (!BundleIntegrity.IsPristine(resolved.Path, entry.Hash))
            modified.Add((entry.Name, expected.Contains(entry.Name)));
    }
    if (fromDownloaded > 0)
        Console.WriteLine($"{fromDownloaded} bundle(s) load from the downloaded cache rather than the shipped one.");

    Console.WriteLine($"{modified.Count} bundle(s) differ from the hash the game recorded for them"
        + (missing > 0 ? $", {missing} not downloaded" : "") + ".");
    foreach (var (bundle, known) in modified.OrderBy(m => m.Bundle))
        Console.WriteLine($"  {TextColumn.Pad(bundle, 40)} {(known ? "modified by an installed mod" : "modified by something else")}");
    if (modified.Any(m => !m.Known))
        Console.WriteLine("\nBundles in the second group have no backup here. Verify the game's files "
            + "through Steam before installing mods over them.");
    return 0;
}

if (command is "apply" or "mods" or "enable" or "disable" or "remove")
{
    var store = new ModStore(game);
    var applier = new ModApplier(game, store) { Force = args.Contains("--force") };

    if (command == "mods")
    {
        var installed = store.Read();
        if (installed.Count == 0) Console.WriteLine("Nothing installed.");
        foreach (var mod in installed.OrderBy(m => m.InstalledAt))
            Console.WriteLine($"{(mod.Enabled ? "[on ]" : "[off]")} {TextColumn.Pad(mod.Id, 28)} "
                + $"{TextColumn.Pad(mod.Name, 30)} {mod.Version,-8} "
                + $"{mod.TouchedBundles.Count} bundle(s), installed {mod.InstalledAt:yyyy-MM-dd} for {mod.GameVersion}");
        Console.WriteLine($"\nBackups: {store.BackupRoot}");
        return 0;
    }

    var target = positional.FirstOrDefault();
    if (target is null)
    {
        Console.Error.WriteLine($"{command} requires " + (command == "apply" ? "a .pgmod path." : "a mod id."));
        return 2;
    }

    try
    {
        var version = bundles.Context.HasClassDatabase ? GameVersion.Read(bundles.Context, game) : "unknown";
        var result = command switch
        {
            "apply" => applier.Install(target, version),
            "enable" => applier.SetEnabled(target, true),
            "disable" => applier.SetEnabled(target, false),
            "remove" => applier.Remove(target),
            _ => throw new UnreachableException(),
        };

        foreach (var operation in result.Applied)
            Console.WriteLine($"  {operation.Mod}  {operation.Op}  {operation.Target}  "
                + $"[{operation.Detail}{(operation.ResolvedByPathId ? "" : ", matched by name")}]");
        if (result.Restored.Count > 0)
            Console.WriteLine($"  restored from backup: {string.Join(", ", result.Restored)}");
        if (result.PrunedBackups.Count > 0)
            Console.WriteLine($"  removed stale backups: {string.Join(", ", result.PrunedBackups)}");
        foreach (var failure in result.Failed) Console.Error.WriteLine($"  FAILED {failure}");

        Console.WriteLine($"\n{result.Applied.Count} operation(s) applied, {result.Failed.Count} failed.");
        return result.Failed.Count > 0 ? 1 : 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
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
    var exporter = new WeaponExporter(bundles);
    var asWorkspace = args.Contains("--workspace");
    var export = asWorkspace
        ? exporter.ExportAsWorkspace(tree, outputRoot,
            Option("author") ?? "",
            bundles.Context.HasClassDatabase ? GameVersion.Read(bundles.Context, game) : null)
        : exporter.Export(tree, outputRoot);

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
    if (asWorkspace)
    {
        var manifest = Workspace.Read(export.Directory);
        Console.WriteLine();
        Console.WriteLine($"  {PackManifest.FileName} lists {manifest.Operations.Count} replaceable files.");
        Console.WriteLine("  Edit any of them, then run:");
        Console.WriteLine($"    pgassettool pack \"{export.Directory}\"");
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
