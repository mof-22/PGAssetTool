using System.IO.Compression;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Mods;

public sealed record AppliedOperation(string Mod, string Op, AssetAddress Target, string Detail, bool ResolvedByPathId);

public sealed record ReconcileResult(
    IReadOnlyList<string> Restored,
    IReadOnlyList<AppliedOperation> Applied,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> PrunedBackups);

/// Writes the enabled mods into the game's bundles.
///
/// Every change goes through a full reconcile: each affected bundle is restored from its backup and
/// the enabled mods are then applied in the order they were installed. Toggling one mod off by
/// restoring its bundles would also undo any other mod sharing them, and reapplying on top of
/// already-modified bundles would stack changes; rebuilding from the originals avoids both.
public sealed class ModApplier(GameInstallation game, ModStore store)
{
    /// Accept a bundle that is already modified as the baseline for its backup.
    public bool Force { get; init; }

    public ReconcileResult Install(string packPath, string gameVersion)
    {
        var manifest = PackBuilder.ReadManifest(packPath);
        var mods = store.Read();
        mods.RemoveAll(m => m.Id == manifest.Id);
        mods.Add(new InstalledMod
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Author = manifest.Author,
            Version = manifest.Version,
            PackPath = Path.GetFullPath(packPath),
            InstalledAt = DateTimeOffset.Now,
            GameVersion = gameVersion,
            Enabled = true,
            TouchedBundles = manifest.Operations
                .Select(o => o.Target.Container)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c, _ => "", StringComparer.OrdinalIgnoreCase),
        });
        store.Write(mods);
        return Reconcile();
    }

    public ReconcileResult SetEnabled(string id, bool enabled)
    {
        var mods = store.Read();
        var index = mods.FindIndex(m => m.Id == id);
        if (index < 0) throw new KeyNotFoundException($"No mod with id '{id}' is installed.");
        mods[index] = mods[index] with { Enabled = enabled };
        store.Write(mods);
        return Reconcile();
    }

    public ReconcileResult Remove(string id)
    {
        var mods = store.Read();
        if (mods.RemoveAll(m => m.Id == id) == 0)
            throw new KeyNotFoundException($"No mod with id '{id}' is installed.");
        store.Write(mods);
        return Reconcile();
    }

    /// Rebuilds every affected bundle from its backup, then applies the enabled mods in order.
    /// Also runs after a game update, which is when backups for superseded bundle versions go stale.
    public ReconcileResult Reconcile()
    {
        var hashes = game.ReadManifest().ToDictionary(e => e.Name, e => e.Hash, StringComparer.OrdinalIgnoreCase);
        var mods = store.Read();
        var restored = new List<string>();
        var applied = new List<AppliedOperation>();
        var failed = new List<string>();

        foreach (var bundle in mods.SelectMany(m => m.TouchedBundles.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!hashes.TryGetValue(bundle, out var hash)) continue;
            foreach (var (cache, live) in game.LocateAll(bundle, hash))
                if (store.RestoreIfBackedUp(cache.Kind, bundle, hash, live))
                    restored.Add($"{bundle} ({cache.Kind})");
        }

        var touchedByMod = new Dictionary<string, Dictionary<string, string>>();

        foreach (var mod in mods.Where(m => m.Enabled).OrderBy(m => m.InstalledAt))
        {
            if (!File.Exists(mod.PackPath))
            {
                failed.Add($"{mod.Id}: pack file is gone ({mod.PackPath})");
                continue;
            }
            touchedByMod[mod.Id] = ApplyOne(mod, hashes, applied, failed);
        }

        // Record which bundle version each mod actually wrote to, so a later update is detectable.
        store.Write(mods.Select(m => touchedByMod.TryGetValue(m.Id, out var touched)
            ? m with { TouchedBundles = touched }
            : m));

        var pruned = store.PruneStaleBackups(hashes);
        return new ReconcileResult(restored, applied, failed, pruned);
    }

    private Dictionary<string, string> ApplyOne(
        InstalledMod mod, IReadOnlyDictionary<string, string> hashes,
        List<AppliedOperation> applied, List<string> failed)
    {
        var manifest = PackBuilder.ReadManifest(mod.PackPath);
        using var archive = ZipFile.OpenRead(mod.PackPath);
        var staging = Directory.CreateTempSubdirectory("pgassettool-apply");
        var touched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var group in manifest.Operations.GroupBy(o => o.Target.Container, StringComparer.OrdinalIgnoreCase))
            {
                if (!hashes.TryGetValue(group.Key, out var hash))
                {
                    failed.Add($"{mod.Id}: no bundle named '{group.Key}' in this installation");
                    continue;
                }

                // Every copy gets the change. The game prefers the downloaded one, but which copies
                // exist changes over time, and a mod that quietly stops working when the cache does
                // is worse than doing the work twice.
                var copies = game.LocateAll(group.Key, hash).ToList();
                if (copies.Count == 0)
                {
                    failed.Add($"{mod.Id}: no copy of '{group.Key}' is present in any cache");
                    continue;
                }

                foreach (var (cache, live) in copies)
                {
                    // Backing up a bundle something else already edited would record that edit as
                    // the original, leaving no way back to the shipped file.
                    if (!store.HasBackup(cache.Kind, group.Key, hash)
                        && !BundleIntegrity.IsPristine(live, hash) && !Force)
                    {
                        failed.Add($"{mod.Id}: '{group.Key}' in the {cache.Kind.ToString().ToLowerInvariant()} "
                            + "cache has already been modified by something else and there is no backup "
                            + "of it. Restore it, or pass --force to accept its contents as the original.");
                        continue;
                    }
                    store.Backup(cache.Kind, group.Key, hash, live);

                    var rewritten = Path.Combine(staging.FullName, $"{cache.Kind}_{group.Key}");
                    if (!EditBundle(mod, live, rewritten, group, archive, staging.FullName, applied, failed))
                        continue;

                    File.Copy(rewritten, live, overwrite: true);
                    touched[group.Key] = hash;
                }
            }
        }
        finally
        {
            staging.Delete(recursive: true);
        }
        return touched;
    }

    private bool EditBundle(
        InstalledMod mod, string live, string output, IEnumerable<PackOperation> operations,
        ZipArchive archive, string staging, List<AppliedOperation> applied, List<string> failed)
    {
        using var editor = new BundleEditor(live);
        var index = new ContainerIndex(editor.Context);
        var changed = false;

        foreach (var operation in operations)
        {
            var info = index.Resolve(operation.Target, editor.File, out var byPathId);
            if (info is null) { failed.Add($"{mod.Id}: {operation.Target} not found"); continue; }

            var field = editor.Read(info);
            if (field is null) { failed.Add($"{mod.Id}: {operation.Target} could not be read"); continue; }

            var entry = archive.GetEntry(operation.Source);
            if (entry is null) { failed.Add($"{mod.Id}: pack has no '{operation.Source}'"); continue; }

            var source = Path.Combine(staging, Path.GetFileName(operation.Source));
            entry.ExtractToFile(source, overwrite: true);

            try
            {
                var change = operation.Op switch
                {
                    PackOperations.ReplaceTexture => TextureImporter.Replace(field, source),
                    _ => throw new NotSupportedException($"unknown operation '{operation.Op}'"),
                };
                editor.Stage(info, field);
                changed = true;
                applied.Add(new AppliedOperation(mod.Id, operation.Op, operation.Target,
                    change.ToString(), byPathId));
            }
            catch (Exception ex)
            {
                failed.Add($"{mod.Id}: {operation.Target} {ex.Message}");
            }
        }

        if (changed) editor.Save(output);
        return changed;
    }
}
