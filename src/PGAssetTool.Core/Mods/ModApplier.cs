using System.IO.Compression;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
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
        var displaced = new List<string>();

        // Each pack claims its assets as it goes in, so a later one in the same batch displaces an
        // earlier one the same way it displaces something installed last week.
        //
        // Installing is how a mod usually comes to be on, and it was the one route that skipped
        // this: turning a mod on stood the others down, and installing one enrolled it enabled and
        // said nothing. Two skins for #401 arrived both on, both writing the same texture, and only
        // one of them was in the game.
        var enrolled = packPaths.Select(p => Enrol(mods, p, gameVersion)).ToList();
        displaced.AddRange(StandAside(mods, enrolled));

        Displaced = displaced.Distinct().ToList();
        store.Write(mods);
        return Reconcile();
    }

    /// Puts a pack in the ledger, enabled, and answers its id.
    private string Enrol(List<InstalledMod> mods, string packPath, string gameVersion)
    {
        var manifest = PackBuilder.ReadManifest(packPath);
        mods.RemoveAll(m => m.Id == manifest.Id);
        mods.Add(new InstalledMod
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Author = manifest.Author,
            Version = manifest.Version,
            PackPath = store.Keep(packPath, manifest.Subject),
            InstalledAt = DateTimeOffset.Now,
            GameVersion = gameVersion,
            Enabled = true,
            Subject = manifest.Subject,
            TouchedBundles = manifest.Operations
                .Select(o => o.Target.Container)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(c => c, _ => "", StringComparer.OrdinalIgnoreCase),
        });

        return manifest.Id;
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

        if (enabled) Displaced = StandAside(mods, ids);

        store.Write(mods);
        return Reconcile();
    }

    /// What the last turning-on turned off, so the caller can say so. Names, not ids.
    public IReadOnlyList<string> Displaced { get; private set; } = [];

    /// Turns off whatever else was writing the same assets as the mods just turned on.
    ///
    /// Two mods that write the same texture do not both take effect; the reconcile applies them in
    /// the order they were installed and the last one wins, silently. Which of two skins for one
    /// weapon a player is actually running was then a question the manager could not answer, and
    /// turning one on appeared to do nothing at all.
    ///
    /// Judged on the assets themselves rather than on the item they belong to. Two mods for one
    /// weapon that touch nothing in common — a new model and a new shop icon, say — are a
    /// combination worth having, and the whole point of this is that the ones which cannot stand
    /// together are exactly the ones that write over each other.
    ///
    /// Read out of the packs at the moment it is asked rather than kept in the ledger. It is a
    /// handful of small files, read once per turning-on, against a reconcile that rewrites every
    /// bundle the mods touch — and reading them is what makes the answer true for a mod installed
    /// before any of this existed.
    ///
    /// The mods being turned on are held to this as well as everyone else, which is the half that
    /// was missing: select every row and turn them on, and thirty-nine mods all went on together,
    /// rivals included. Whoever came last wins, because that is the order a reconcile applies them
    /// in — so the answer here and the file that ends up in the game agree.
    private List<string> StandAside(List<InstalledMod> mods, IReadOnlyList<string> ids)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var displaced = new List<string>();
        var writes = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        List<string> WrittenBy(InstalledMod mod)
        {
            if (writes.TryGetValue(mod.Id, out var known)) return known;
            return writes[mod.Id] = Writes(mod).ToList();
        }

        // Latest first: the last one applied is the one actually in the game, so it is the one that
        // keeps its claim and the earlier ones stand down.
        var turningOn = ids
            .Select(id => mods.FindIndex(m => m.Id == id))
            .Where(at => at >= 0)
            .OrderByDescending(at => mods[at].InstalledAt)
            .ThenByDescending(at => at)
            .ToList();

        foreach (var at in turningOn)
        {
            var mine = WrittenBy(mods[at]);
            if (mine.Any(claimed.Contains))
            {
                displaced.Add(mods[at].Name);
                mods[at] = mods[at] with { Enabled = false };
                continue;
            }

            foreach (var asset in mine) claimed.Add(asset);
        }

        if (claimed.Count == 0) return displaced;

        for (var i = 0; i < mods.Count; i++)
        {
            if (!mods[i].Enabled || ids.Contains(mods[i].Id)) continue;
            if (!WrittenBy(mods[i]).Any(claimed.Contains)) continue;

            displaced.Add(mods[i].Name);
            mods[i] = mods[i] with { Enabled = false };
        }

        return displaced;
    }

    /// Every asset a mod writes, as a name that means the same thing in two different packs.
    private static IEnumerable<string> Writes(InstalledMod? mod)
    {
        if (mod is null || !File.Exists(mod.PackPath)) return [];

        try
        {
            return PackBuilder.ReadManifest(mod.PackPath).Operations
                .Select(o => $"{o.Target.Container}:{o.Target.Class}:{o.Target.Name}:{o.Target.PathId}")
                .ToList();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A pack that will not open cannot be shown to overlap with anything, and refusing to
            // turn a mod on because another one is unreadable would be the wrong way round.
            return [];
        }
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

        ApplyAll(mods, hashes, touchedByMod, applied, failed, shared);

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

    /// One mod's operations for one bundle, with the pack they came out of held open.
    private sealed record Contribution(InstalledMod Mod, ZipArchive Archive, List<PackOperation> Operations);

    /// Writes every enabled mod into the game, a bundle at a time.
    ///
    /// A bundle at a time and not a mod at a time, which is how this used to work. Each pass over a
    /// bundle decompresses it, edits it and compresses it again, and going mod by mod meant one such
    /// pass per mod — so a bundle that thirty mods write to was rebuilt thirty times over. The work
    /// was never in the edits: with thirty-one mods installed a toggle took thirty-eight seconds, of
    /// which thirty-seven were this, for forty operations in total.
    ///
    /// Install order still decides who wins, because that is the order the contributions are
    /// gathered in and applied in within each bundle. What each mod touched is still recorded per
    /// mod, because that is what tells a later game update which mods need reapplying.
    private void ApplyAll(
        List<InstalledMod> mods, IReadOnlyDictionary<string, string> hashes,
        Dictionary<string, Dictionary<string, string>> touchedByMod,
        List<AppliedOperation> applied, List<string> failed, List<SharedAsset> shared)
    {
        var archives = new List<ZipArchive>();
        var byBundle = new Dictionary<string, List<Contribution>>(StringComparer.OrdinalIgnoreCase);
        var staging = Directory.CreateTempSubdirectory("pgassettool-apply");

        try
        {
            foreach (var mod in mods.Where(m => m.Enabled).OrderBy(m => m.InstalledAt))
            {
                touchedByMod[mod.Id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!File.Exists(mod.PackPath))
                {
                    failed.Add($"{mod.Id}: pack file is gone ({mod.PackPath})");
                    continue;
                }

                // Held open for the whole run rather than reopened at each bundle. A pack is a small
                // file read whole into memory, and a mod that writes to four bundles would otherwise
                // be read four times.
                var archive = PackFile.Open(mod.PackPath);
                archives.Add(archive);

                foreach (var group in PackBuilder.ReadManifest(mod.PackPath).Operations
                             .GroupBy(o => o.Target.Container, StringComparer.OrdinalIgnoreCase))
                {
                    if (!byBundle.TryGetValue(group.Key, out var parts))
                        byBundle[group.Key] = parts = [];

                    parts.Add(new Contribution(mod, archive, group.ToList()));
                }
            }

            foreach (var (bundle, parts) in byBundle)
            {
                if (!hashes.TryGetValue(bundle, out var hash))
                {
                    foreach (var part in parts)
                        failed.Add($"{part.Mod.Id}: no bundle named '{bundle}' in this installation");
                    continue;
                }

                // Only the copy the game would actually load is worth writing to. Editing the shipped
                // copy of a bundle the downloaded cache claims changes nothing and says nothing.
                if (game.Resolve(bundle, hash) is not { } resolved)
                {
                    foreach (var part in parts)
                        failed.Add($"{part.Mod.Id}: no copy of '{bundle}' is present in any cache");
                    continue;
                }
                var (cache, live) = (resolved.Cache, resolved.Path);

                // Backing up a bundle something else already edited would record that edit as the
                // original, leaving no way back to the shipped file.
                if (!store.HasBackup(cache, bundle, hash) && !BundleIntegrity.IsPristine(live, hash) && !Force)
                {
                    foreach (var part in parts)
                        failed.Add($"{part.Mod.Id}: '{bundle}' in the {cache.ToString().ToLowerInvariant()} cache "
                            + "has already been modified by something else and there is no backup of it. "
                            + "Restore it, or pass --force to accept its contents as the original.");
                    continue;
                }
                store.Backup(cache, bundle, hash, live);

                var rewritten = Path.Combine(staging.FullName, bundle);
                var wrote = EditBundle(live, rewritten, parts, staging.FullName, applied, failed, shared);
                if (wrote.Count == 0) continue;

                File.Copy(rewritten, live, overwrite: true);
                foreach (var id in wrote) touchedByMod[id][bundle] = hash;
            }
        }
        finally
        {
            foreach (var archive in archives) archive.Dispose();
            staging.Delete(recursive: true);
        }
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

    /// Whether an operation names a class this tool will not write, from what the pack says about
    /// it rather than from the asset — the target is refused before anything is opened.
    private static bool Refuses(PackOperation operation, out string why)
    {
        why = "";
        if (!Enum.TryParse<AssetClassID>(operation.Target.Class, out var cls)) return false;
        if (Replaceable.CanWriteBack(cls)) return false;

        // The file it would have been written from, when the asset has no name of its own — which
        // components generally do not, so the target's name would be an empty pair of quotes.
        why = Replaceable.WhyRefused(
            cls, operation.Target.Name.Length > 0 ? operation.Target.Name : operation.Source);
        return true;
    }

    /// Applies every mod's share of one bundle, in one pass, and answers which of them wrote to it.
    private List<string> EditBundle(
        string live, string output, IReadOnlyList<Contribution> parts,
        string staging, List<AppliedOperation> applied, List<string> failed, List<SharedAsset> shared)
    {
        using var editor = new BundleEditor(live);
        var index = new ContainerIndex(editor.Context);
        var wrote = new List<string>();

        // Built once for the bundle, and only if something is actually replaced in it. Under half a
        // second on the largest bundle in the game, which an install can afford.
        var usage = new Lazy<BundleUsage>(() => BundleUsage.Build(editor.Context, editor.File));

        foreach (var part in parts)
        {
            var mod = part.Mod;
            var archive = part.Archive;
            var changed = false;

            // Additions go in first, and everything else afterwards, because the id an added asset ends
            // up with is what the pointers naming it are filled in from. Doing it in manifest order
            // would work for a manifest written in that order and fail quietly for one that was not.
            var ordered = part.Operations;
            var newIds = AddAssets(mod, editor, ordered, archive, staging, applied, failed, ref changed);

            foreach (var operation in ordered.Where(o => o.Op != PackOperations.AddAsset))
            {
                // Asked before the asset is read, and asked here rather than only where packs are
                // built: a pack that arrives from somewhere else has never been past that check.
                if (Refuses(operation, out var why)) { failed.Add($"{mod.Id}: {why}"); continue; }

                var info = index.Resolve(operation.Target, editor.File, out var byPathId);
                if (info is null) { failed.Add($"{mod.Id}: {operation.Target} not found"); continue; }

                var field = editor.Read(info);
                if (field is null) { failed.Add($"{mod.Id}: {operation.Target} could not be read"); continue; }

                var entry = archive.GetEntry(operation.Source);
                if (entry is null) { failed.Add($"{mod.Id}: pack has no '{operation.Source}'"); continue; }

                var source = Path.Combine(staging, Path.GetFileName(operation.Source));
                PackBuilder.WriteEntry(entry, source, PackBuilder.MostPerFile, $"'{operation.Source}'");

                try
                {
                    object change;
                    if (operation.Op == PackOperations.ReplaceRaw)
                    {
                        // Staged from the file's own bytes rather than from the asset that was read:
                        // the point of a raw replacement is that the bytes are the asset, and there is
                        // nothing of the old one to carry over.
                        change = RawImporter.Replace(editor, info, source, operation, newIds);
                    }
                    else
                    {
                        change = operation.Op switch
                        {
                            PackOperations.ReplaceTexture =>
                                TextureImporter.Replace(field, source, OriginalPixels(editor, field)),
                            PackOperations.ReplaceMesh => MeshImporter.Replace(field, GltfMeshReader.Read(source)),
                            PackOperations.ReplaceAudio => AudioImporter.Replace(field, source, (into, bank) => editor.AppendToStream(into, bank)),
                            _ => throw new NotSupportedException($"unknown operation '{operation.Op}'"),
                        };
                        editor.Stage(info, field);
                    }
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

            if (changed) wrote.Add(mod.Id);
        }

        // Written once, whatever went into it. Saving per mod would put the bundle back through a
        // whole rebuild for each of them, which is the cost this exists to avoid.
        if (wrote.Count > 0) editor.Save(output);
        return wrote;
    }

    /// Puts the pack's added assets into the bundle, and answers what id each one got.
    ///
    /// The id recorded in the manifest is the one the author's editor gave the asset, and it is
    /// tried first: keeping it means the pointers in the rest of the pack are already right, and it
    /// makes an applied bundle match what the author tested. But it is only a preference. Nothing
    /// reserved that number in the player's bundle, and a game update can put a real asset there,
    /// so when it is taken the asset gets another and everything naming it is rewritten instead.
    private static Dictionary<string, long> AddAssets(
        InstalledMod mod, BundleEditor editor, List<PackOperation> operations,
        ZipArchive archive, string staging, List<AppliedOperation> applied, List<string> failed,
        ref bool changed)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        var additions = operations.Where(o => o.Op == PackOperations.AddAsset).ToList();
        if (additions.Count == 0) return ids;

        // Two passes over the additions, because one added asset may point at another: every id is
        // settled before any pointer is filled in.
        var staged = new List<(PackOperation Operation, AssetFileInfo Info, AssetClassID Class, byte[] Bytes)>();

        foreach (var operation in additions)
        {
            // Adding one is writing one. Asked here as well, because additions are handled before
            // everything else and never reach the loop that asks below.
            if (Refuses(operation, out var why)) { failed.Add($"{mod.Id}: {why}"); continue; }

            if (operation.NewId is not { Length: > 0 } newId)
            {
                failed.Add($"{mod.Id}: {operation.Target} adds an asset without saying what to call it");
                continue;
            }
            if (ids.ContainsKey(newId))
            {
                failed.Add($"{mod.Id}: the pack adds two assets both called '{newId}'");
                continue;
            }
            if (!Enum.TryParse<AssetClassID>(operation.Target.Class, out var cls))
            {
                failed.Add($"{mod.Id}: {operation.Target} names no class this build knows");
                continue;
            }
            if (archive.GetEntry(operation.Source) is not { } entry)
            {
                failed.Add($"{mod.Id}: pack has no '{operation.Source}'");
                continue;
            }

            var source = Path.Combine(staging, Path.GetFileName(operation.Source));
            PackBuilder.WriteEntry(entry, source, PackBuilder.MostPerFile, $"'{operation.Source}'");
            var bytes = File.ReadAllBytes(source);

            if (!AssetAddition.HasType(editor.File, cls))
            {
                failed.Add($"{mod.Id}: '{operation.Target.Container}' carries no type information "
                    + $"for {cls}, so '{newId}' cannot be added to it");
                continue;
            }

            var pathId = FreeId(editor.File, operation.Target.PathId, mod.Id, newId);
            var info = AssetFileInfo.Create(editor.File.file, pathId, (int)cls, null!, preferEditor: false);
            info.SetNewData(bytes);
            editor.File.file.Metadata.AssetInfos.Add(info);

            ids[newId] = pathId;
            staged.Add((operation, info, cls, bytes));
        }

        // The lookup the file answers path id questions from is built from the list, so it has to
        // be rebuilt now that the list has grown. Everything below reads through it.
        editor.File.file.GenerateQuickLookup();

        foreach (var (operation, info, cls, bytes) in staged)
        {
            var newId = operation.NewId!;
            try
            {
                var field = editor.Read(info)
                    ?? throw new InvalidDataException($"'{newId}' could not be read as a {cls}.");

                if (AssetAddition.Rejects(editor.Context, editor.File, cls, field, "") is { } refusal)
                    throw new InvalidDataException(refusal);

                var repointed = RawImporter.Repoint(field, operation, ids);

                // The name in the manifest is what the asset should be called here, which is not
                // always what it was called where it was built: two shaders in one file cannot
                // share a name, and renaming is the way out of that.
                var named = Rename(field, cls, operation.Target.Name);
                if (Named(editor, cls, operation.Target.Name, info.PathId) is { } clash)
                    throw new InvalidDataException(
                        $"'{operation.Target.Container}' already has a {cls} called "
                        + $"'{operation.Target.Name}' (path id {clash}).");

                if (repointed > 0 || named) editor.Stage(info, field);
                changed = true;

                applied.Add(new AppliedOperation(mod.Id, operation.Op, operation.Target,
                    $"added as {cls} {info.PathId}"
                        + (info.PathId == operation.Target.PathId ? "" : " (its own id was taken)")
                        + (repointed > 0 ? $", {repointed} pointer(s) repointed" : ""),
                    ResolvedByPathId: info.PathId == operation.Target.PathId));
            }
            catch (Exception ex)
            {
                failed.Add($"{mod.Id}: {operation.Target} {ex.Message}");
                editor.File.file.Metadata.AssetInfos.Remove(info);
                editor.File.file.GenerateQuickLookup();
                ids.Remove(newId);
            }
        }

        return ids;
    }

    private static bool Rename(AssetTypeValueField field, AssetClassID cls, string name)
    {
        if (name.Length == 0 || AssetNaming.NameOf(field, cls) == name) return false;
        return AssetNaming.TryRename(field, cls, name);
    }

    /// Another asset of the same class already using the name, if there is one.
    private static long? Named(BundleEditor editor, AssetClassID cls, string name, long except)
    {
        if (name.Length == 0) return null;

        foreach (var info in editor.File.file.AssetInfos)
        {
            if (info.TypeId != (int)cls || info.PathId == except) continue;
            if (AssetNaming.NameOf(editor.Read(info), cls) == name) return info.PathId;
        }
        return null;
    }

    /// The path id an added asset gets: the one it asks for while that is free, and otherwise one
    /// derived from the pack and the asset's handle, so the same pack applied twice lands on the
    /// same number and a bundle can be compared against itself.
    private static long FreeId(AssetsFileInstance file, long? preferred, string modId, string newId)
    {
        if (preferred is { } wanted && wanted != 0 && file.file.GetAssetInfo(wanted) is null) return wanted;

        var seed = $"{modId}/{newId}";
        for (var attempt = 0; ; attempt++)
        {
            var candidate = Stable($"{seed}#{attempt}");
            if (candidate != 0 && file.file.GetAssetInfo(candidate) is null) return candidate;
        }
    }

    private static long Stable(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return BitConverter.ToInt64(hash, 0);
    }
}
