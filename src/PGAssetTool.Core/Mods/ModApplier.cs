using System.IO.Compression;
using AssetsTools.NET;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Import.Audio;
using PGAssetTool.Core.Import.Meshes;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Mods;

public sealed record AppliedOperation(string Mod, string Op, AssetAddress Target, string Detail, bool ResolvedByPathId);

public sealed record ReconcileResult(
    IReadOnlyList<string> Restored,
    IReadOnlyList<AppliedOperation> Applied,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> PrunedBackups,
    IReadOnlyList<SharedAsset> Shared);

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
        => Install([packPath], gameVersion);

    /// Installs several packs and rebuilds the game once, rather than once per pack.
    ///
    /// A reconcile restores every modified bundle and reapplies everything enabled, so running it
    /// per pack does the same whole-game work as many times as there are packs — and leaves the
    /// game briefly in a state that has some of them but not the rest.
    public ReconcileResult Install(IReadOnlyList<string> packPaths, string gameVersion)
    {
        var mods = store.Read();
        foreach (var packPath in packPaths) Enrol(mods, packPath, gameVersion);
        store.Write(mods);
        return Reconcile();
    }

    private void Enrol(List<InstalledMod> mods, string packPath, string gameVersion)
    {
        var manifest = PackBuilder.ReadManifest(packPath);
        mods.RemoveAll(m => m.Id == manifest.Id);
        mods.Add(new InstalledMod
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Author = manifest.Author,
            Version = manifest.Version,
            PackPath = store.Keep(packPath),
            InstalledAt = DateTimeOffset.Now,
            GameVersion = gameVersion,
            Enabled = true,
            TouchedBundles = manifest.Operations
                .Select(o => o.Target.Container)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c, _ => "", StringComparer.OrdinalIgnoreCase),
        });
    }

    public ReconcileResult SetEnabled(string id, bool enabled) => SetEnabled([id], enabled);

    public ReconcileResult SetEnabled(IReadOnlyList<string> ids, bool enabled)
    {
        var mods = store.Read();
        foreach (var id in ids)
        {
            var index = mods.FindIndex(m => m.Id == id);
            if (index < 0) throw new KeyNotFoundException($"No mod with id '{id}' is installed.");
            mods[index] = mods[index] with { Enabled = enabled };
        }
        store.Write(mods);
        return Reconcile();
    }

    public ReconcileResult Remove(string id) => Remove([id]);

    public ReconcileResult Remove(IReadOnlyList<string> ids)
    {
        var mods = store.Read();
        foreach (var id in ids)
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
        var shared = new List<SharedAsset>();

        // Restore from every backup there is, not from what the installed mods happen to name.
        // Uninstalling drops the mod before its bundles are put back, so by the time the reconcile
        // runs the list no longer mentions them and the modified bundle would be left in place.
        foreach (var (cache, bundle, hash) in store.BackedUp())
        {
            var live = LivePathIn(cache, bundle, hash);
            if (live is null || !File.Exists(live)) continue;

            // Nothing to put back if it is already what the game shipped. A backup outlives the mod
            // that caused it — nothing prunes one for being unneeded — so this loop kept copying
            // every bundle the tool had ever touched over an identical copy of itself: seventy
            // megabytes of writing, on every install and every toggle, to no effect. Hashing the
            // one file is a fraction of the cost of rewriting it, and a write that never happens is
            // a write that cannot collide with something still reading the file.
            if (BundleIntegrity.IsPristine(live, hash)) continue;

            if (store.RestoreIfBackedUp(cache, bundle, hash, live))
                restored.Add($"{bundle} ({cache.ToString().ToLowerInvariant()})");
        }

        var touchedByMod = new Dictionary<string, Dictionary<string, string>>();

        foreach (var mod in mods.Where(m => m.Enabled).OrderBy(m => m.InstalledAt))
        {
            if (!File.Exists(mod.PackPath))
            {
                failed.Add($"{mod.Id}: pack file is gone ({mod.PackPath})");
                continue;
            }
            touchedByMod[mod.Id] = ApplyOne(mod, hashes, applied, failed, shared);
        }

        // Record which bundle version each mod actually wrote to, so a later update is detectable.
        store.Write(mods.Select(m => touchedByMod.TryGetValue(m.Id, out var touched)
            ? m with { TouchedBundles = touched }
            : m));

        var pruned = store.PruneStaleBackups(hashes);
        return new ReconcileResult(restored, applied, failed, pruned, shared);
    }

    /// Where a backup taken from a given cache belongs, which is not necessarily the copy the game
    /// now loads: once the downloaded cache claims a bundle it wins, and the shipped copy beneath it
    /// still has to be put back.
    private string? LivePathIn(CacheKind cache, string bundle, string hash) => cache switch
    {
        CacheKind.Downloaded => game.Downloaded?.ClaimedPath(bundle),
        _ => Path.Combine(game.BundlesDirectory, bundle, hash, bundle),
    };

    private Dictionary<string, string> ApplyOne(
        InstalledMod mod, IReadOnlyDictionary<string, string> hashes,
        List<AppliedOperation> applied, List<string> failed, List<SharedAsset> shared)
    {
        var manifest = PackBuilder.ReadManifest(mod.PackPath);
        using var archive = PackFile.Open(mod.PackPath);
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

                // Only the copy the game would actually load is worth writing to. Editing the shipped
                // copy of a bundle the downloaded cache claims changes nothing and says nothing.
                if (game.Resolve(group.Key, hash) is not { } resolved)
                {
                    failed.Add($"{mod.Id}: no copy of '{group.Key}' is present in any cache");
                    continue;
                }
                var (cache, live) = (resolved.Cache, resolved.Path);

                // Backing up a bundle something else already edited would record that edit as the
                // original, leaving no way back to the shipped file.
                if (!store.HasBackup(cache, group.Key, hash) && !BundleIntegrity.IsPristine(live, hash) && !Force)
                {
                    failed.Add($"{mod.Id}: '{group.Key}' in the {cache.ToString().ToLowerInvariant()} cache "
                        + "has already been modified by something else and there is no backup of it. "
                        + "Restore it, or pass --force to accept its contents as the original.");
                    continue;
                }
                store.Backup(cache, group.Key, hash, live);

                var rewritten = Path.Combine(staging.FullName, group.Key);
                if (!EditBundle(mod, live, rewritten, group, archive, staging.FullName, applied, failed, shared)) continue;

                File.Copy(rewritten, live, overwrite: true);
                touched[group.Key] = hash;
            }
        }
        finally
        {
            staging.Delete(recursive: true);
        }
        return touched;
    }

    /// The texture as the game holds it, decoded, so a replacement that arrives without an alpha
    /// channel can be given the original one back.
    ///
    /// Nearly every texture keeps its pixels in a sibling stream rather than on the object, so this
    /// has to go and fetch them. Failing is not worth stopping an apply over: all that is lost is
    /// the chance to reuse an alpha channel.
    private static byte[]? OriginalPixels(BundleEditor editor, AssetTypeValueField field)
    {
        try
        {
            var texture = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(field);
            var payload = texture.pictureData;

            var stream = field["m_StreamData"];
            if (!stream.IsDummy && stream["path"].AsString.Length > 0)
                payload = editor.ReadStream(
                    stream["path"].AsString, stream["offset"].AsLong, stream["size"].AsLong);

            return payload is { Length: > 0 } ? texture.DecodeTextureRaw(payload, useBgra: true) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool EditBundle(
        InstalledMod mod, string live, string output, IEnumerable<PackOperation> operations,
        ZipArchive archive, string staging, List<AppliedOperation> applied, List<string> failed,
        List<SharedAsset> shared)
    {
        using var editor = new BundleEditor(live);
        var index = new ContainerIndex(editor.Context);
        var changed = false;

        // Built once for the bundle, and only if something is actually replaced in it. Under half a
        // second on the largest bundle in the game, which an install can afford.
        var usage = new Lazy<BundleUsage>(() => BundleUsage.Build(editor.Context, editor.File));

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
                object change = operation.Op switch
                {
                    PackOperations.ReplaceTexture =>
                        TextureImporter.Replace(field, source, OriginalPixels(editor, field)),
                    PackOperations.ReplaceMesh => MeshImporter.Replace(field, GltfMeshReader.Read(source)),
                    PackOperations.ReplaceAudio => AudioImporter.Replace(field, source, (into, bank) => editor.AppendToStream(into, bank)),
                    _ => throw new NotSupportedException($"unknown operation '{operation.Op}'"),
                };
                editor.Stage(info, field);
                changed = true;
                if (SharedAssets.Check(usage.Value, operation.Target, info.PathId) is { } also)
                    shared.Add(also);

                applied.Add(new AppliedOperation(mod.Id, operation.Op, operation.Target,
                    change.ToString()!, byPathId));
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
