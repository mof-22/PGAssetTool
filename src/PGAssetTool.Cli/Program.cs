using System.Diagnostics;
using System.Text;
using AssetsTools.NET.Extra;
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
                               WAV, meshes as glTF, the object graph as JSON. With --workspace, also writes a
                               pgmod.json naming every replaceable file.
          pack [<directory>]   Build a .pgmod from a workspace. Only files edited since the
                               extract are included.

          convert <path>       Turn raw .dat assets exported by an asset editor into editable
                               formats, recovering each one's type from the game. The result
                               is a packable workspace.

          apply <pack>         Install a .pgmod into the game.
          verify               Check every bundle against the hash the game recorded for it.
          consolidate          Report what emptying the downloaded cache would change. --apply
                               removes only the copies that change nothing.
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
          --opaque             Write textures with no alpha channel. Most of them keep emission
                               rather than transparency there, and an editor opens those as almost
                               invisible. An image brought back without an alpha channel keeps the
                               original one.
          --skin <id or name>  Also write out one of the weapon's skins: its own materials and
                               textures, and the model it brings if it brings one. Off by default,
                               because a weapon carries up to a dozen and writing all of them would
                               multiply the workspace for the sake of the one being worked on.
                               `show` lists what a weapon has.
          --protect            Sign the built pack with a key kept beside the tool, and keep it from
                               opening as a zip. Whoever alters one afterwards shows up as having
                               done so. It stops a casual look inside and nothing more.
        """);
    return 0;
}

if (command == "pack")
{
    var workspace = positional.FirstOrDefault() ?? Directory.GetCurrentDirectory();
    // Named after the mod, not after the directory it was built in. See PackBuilder.FileNameFor.
    var output = Option("out") ?? PackBuilder.OutputFor(workspace, Workspace.Read(workspace));
    try
    {
        // Signed when the workspace asks for it. The CLI has no settings of its own, so a pack that
        // says nothing is built plain — --protect is how to ask from here.
        var manifest = Workspace.Read(workspace);
        using var signer = (manifest.Protect ?? args.Contains("--protect")) ? PackAuthor.Mine() : null;

        var result = PackBuilder.Build(workspace, output, signer);
        Console.WriteLine($"{result.Path}");
        Console.WriteLine($"  {result.Operations} operations, {result.Bytes:N0} bytes");
        foreach (var operation in PackBuilder.ReadManifest(result.Path).Operations)
            Console.WriteLine($"    {operation.Op}  {operation.Target}  <- {operation.Source}");
        if (result.Unchanged.Count > 0)
            Console.WriteLine($"  {result.Unchanged.Count} unedited files left out.");

        // Said here as well as on apply, because the author is the one who can decide whether
        // reaching the other weapons is what they meant. Building the pack itself reads only the
        // workspace, so an installation the tool cannot find costs this warning and nothing else.
        try
        {
            var installation = Option("game") is { } dir
                ? GameInstallation.Open(dir)
                : GameInstallation.OpenDetected();
            using var opened = new BundleSet(installation);
            var named = GameCatalogs.Load(opened, Option("language") ?? GameCatalogs.DefaultLanguage);

            foreach (var also in SharedInPack(opened, PackBuilder.ReadManifest(result.Path)))
                Console.WriteLine($"  shared: {Describe(also, named)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  (could not check what else uses these: {ex.Message})");
        }

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

// Loaded on demand: only the commands that name weapons pay for it.
var weaponNames = new Lazy<GameCatalogs>(
    () => GameCatalogs.Load(bundles, Option("language") ?? GameCatalogs.DefaultLanguage));


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
                + $"@ {TextColumn.Pad(result.Bundle, 12)} -> {Path.GetFileName(asset.Path)}"
                + (result.IsAddition && asset.Path == result.RawPath ? "   (added, not replaced)" : ""));

    Console.WriteLine($"\n{results.Count} of {sources.Count} converted into {destination}");
    Console.WriteLine($"{PackManifest.FileName} has {manifest.Operations.Count} operation(s), "
        + $"{manifest.Operations.Count(o => o.Op == PackOperations.AddAsset)} of them additions.");

    foreach (var operation in manifest.Operations.Where(o => o.Pointers.Count > 0))
        foreach (var pointer in operation.Pointers)
            Console.WriteLine($"  {operation.Target} {pointer.Path} -> '{pointer.NewId}'");

    // An addition whose class had to be guessed, and the guess was not the only one that fits.
    foreach (var result in results.Where(r => r.AlsoFits.Count > 0))
        Console.WriteLine($"\n  '{result.Source.Name}' reads as {result.Class}, but also as "
            + $"{string.Join(" and ", result.AlsoFits)}. Check pgmod.json before packing.");

    var unusable = results.SelectMany(r => r.Written)
        .Where(a => !manifest.Operations.Any(o => o.Source == Path.GetFileName(a.Path)))
        .Select(a => $"{Path.GetFileName(a.Path)} ({a.Class})")
        .ToList();
    if (unusable.Count > 0)
    {
        Console.WriteLine($"\nKept for reading, but not packed — the .dat beside each is what gets "
            + "written back:");
        foreach (var name in unusable.Take(8)) Console.WriteLine($"  {name}");
        if (unusable.Count > 8) Console.WriteLine($"  and {unusable.Count - 8} more");
    }
    Console.WriteLine($"\n  pgassettool pack \"{destination}\"");
    return results.Count == sources.Count ? 0 : 1;
}

if (command == "consolidate")
{
    if (game.Downloaded is null)
    {
        Console.WriteLine("No downloaded cache; everything already loads from the game's own folder.");
        return 0;
    }

    var plan = CacheConsolidation.Plan(game);
    if (plan.Count == 0)
    {
        Console.WriteLine("The downloaded cache claims nothing.");
        return 0;
    }

    foreach (var group in plan.GroupBy(i => i.Verdict))
    {
        Console.WriteLine($"\n{group.Key}  ({group.Count()})");
        Console.WriteLine("  " + group.Key switch
        {
            ConsolidationVerdict.Redundant => "identical to the shipped copy; removing changes nothing",
            ConsolidationVerdict.DownloadedIsDamaged => "differs from its own hash while the shipped copy matches; the shipped one is intact",
            ConsolidationVerdict.ShippedCarriesAMod => "the shipped copy is the edited one; removing these is what lets that edit load",
            ConsolidationVerdict.NewerVersion => "a different version from the one shipped; removing these rolls the game back",
            ConsolidationVerdict.OnlyHere => "nothing shipped under this name and version; removing these loses the content",
            ConsolidationVerdict.BrokenClaim => "claimed with no file; the game logs a read failure for each on every launch",
            _ => "",
        });
        foreach (var item in group.Take(10)) Console.WriteLine($"    {item.Bundle}");
        if (group.Count() > 10) Console.WriteLine($"    and {group.Count() - 10} more");
    }

    var safe = plan.Count(i => i.SafeToRemove);
    var keep = plan.Count - safe;
    Console.WriteLine($"\n{safe} of {plan.Count} can be removed without changing what the game loads.");
    if (keep > 0) Console.WriteLine($"{keep} would change it and are left alone.");

    if (!args.Contains("--apply"))
    {
        Console.WriteLine("\nNothing was changed. Pass --apply to remove the safe ones.");
        Console.WriteLine("Whether the game refills the cache afterwards is not known, so check before relying on it.");
        return 0;
    }

    var removed = CacheConsolidation.Apply(game, plan);
    Console.WriteLine($"\nRemoved {removed.Count}; the ledger now claims {game.Downloaded.Claimed.Count - removed.Count}.");
    return 0;
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
        foreach (var also in result.Shared)
            Console.WriteLine($"  shared: {Describe(also, weaponNames.Value)}");
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
var catalogs = weaponNames.Value;
var catalogTime = timer.ElapsedMilliseconds;

if (command == "weapons")
{
    var filter = positional.FirstOrDefault();
    var matches = (filter is null ? catalogs.Items.Weapons : catalogs.Items.Search(filter, catalogs.Names)).ToList();
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
    var exporter = new WeaponExporter(bundles)
    {
        Opaque = args.Contains("--opaque"),
        Skin = Option("skin"),
    };
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

/// The replaceable objects in a pack that more than one weapon reaches.
///
/// Each bundle is indexed once, so a pack touching several costs one pass over each.
static IEnumerable<SharedAsset> SharedInPack(BundleSet bundles, PackManifest manifest)
{
    foreach (var group in manifest.Operations.GroupBy(o => o.Target.Container, StringComparer.OrdinalIgnoreCase))
    {
        AssetsFileInstance file;
        BundleUsage usage;
        try
        {
            file = bundles.Open(group.Key);
            usage = BundleUsage.Build(bundles.Context, file);
        }
        catch (Exception)
        {
            // A pack can name a bundle this installation does not have; apply reports that properly.
            continue;
        }

        var index = new ContainerIndex(bundles.Context);
        foreach (var operation in group)
        {
            if (index.Resolve(operation.Target, file, out _) is not { } info) continue;
            if (SharedAssets.Check(usage, operation.Target, info.PathId) is { } also) yield return also;
        }
    }
}

/// Names the weapons a shared object reaches.
///
/// The numbers in a prefab name are not the numbers players see — the two sequences agree for six
/// weapons out of 1517 — so reporting them raw would point at the wrong weapon almost every time.
static string Describe(SharedAsset also, GameCatalogs catalogs)
{
    var weapons = also.Weapons
        .Select(number => catalogs.Items.Weapons.FirstOrDefault(w => w.PrefabNumber == number))
        .Where(w => w is not null)
        .Select(w => $"#{w!.GameNumber} {catalogs.Localization.Translate(w.LocalizationKey) ?? w.Slug}")
        .ToList();

    // Anything the catalog does not know — a shared UI material, a prop — is named as it stands.
    var rest = also.Prefabs.Where(p => !p.StartsWith("Weapon", StringComparison.Ordinal)
                                    && !p.StartsWith("Ray", StringComparison.Ordinal));

    return $"{also.Target} is used by {string.Join(", ", weapons.Concat(rest))}. "
        + "Replacing it changes all of them.";
}
