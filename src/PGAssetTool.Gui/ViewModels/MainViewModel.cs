using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Settings;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Gui.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private BundleSet? _bundles;
    private GameCatalogs? _catalogs;
    private WeaponResolver? _resolver;

    /// Guards the bundle reader, which is not safe to use from two threads at once.
    private readonly SemaphoreSlim _reading = new(1, 1);

    /// One answer to the alpha question for the whole window; see AlphaPreference.
    private readonly AlphaPreference _alpha = new();

    public MainViewModel()
    {
        Preview = new PreviewViewModel(_alpha);
        Preview.PropertyChanged += OnPreviewChanged;
        Editor = new EditorViewModel(() => _bundles, _reading, _alpha);
        Editor.PackRequested += BuildPack;

        Manager = new ManagerViewModel(() => _installation);
        Manager.Around = WithReaderClosed;

        // These two live where they are used rather than in the options window — they are worked
        // mid-task, and a round trip through a dialog for each would be worse. Being where they are
        // used is no reason to forget them between runs, though, so the shell writes them out.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.SideBySide) or nameof(EditorViewModel.Linked))
                Remember();
        };
        Manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ManagerViewModel.ConfirmChanges) or nameof(ManagerViewModel.TileSize))
                Remember();
        };
    }

    [ObservableProperty] private string _status = "Looking for the game…";
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private WeaponListItem? _selected;
    [ObservableProperty] private WeaponDetailViewModel? _detail;

    public PreviewViewModel Preview { get; }

    /// The workspace side. Given the same reader and the same lock as everything else, because
    /// there is one BundleSet and it is not safe to use from two places at once.
    public EditorViewModel Editor { get; }

    /// The game side: what is installed and what state the bundles are in.
    public ManagerViewModel Manager { get; }

    /// The resolved weapon behind the current tree, kept so the tree can be rebuilt when the filter
    /// changes without reading the bundles again.
    private WeaponTree? _tree;

    /// The installation itself, which holds no files open — so it survives the reader being put
    /// down to write, which is exactly when the manager needs it.
    private GameInstallation? _installation;

    /// Set while preferences are being applied, so reading them back does not write them out again
    /// or reload the game once per setting.
    private bool _loading;

    /// The preferences as last read or written. Held so extraction knows where to write and whose
    /// name to record without going back to disk for each.
    private ToolSettings _settings = new();

    /// Hides everything that cannot be written back. What counts comes from the import registry.
    [ObservableProperty] private bool _replaceableOnly = true;

    /// The game translation table names are read from, and the languages this installation offers.
    [ObservableProperty] private string _language = "l_en-gb";

    /// Textures written with no alpha channel. The original is put back on the way in.
    [ObservableProperty] private bool _opaqueTextures;

    /// Recorded in the manifest of anything extracted from here on. Editable per pack afterwards.
    [ObservableProperty] private string _author = "";

    /// Whether a built pack is signed and scrambled unless its own manifest says otherwise.
    [ObservableProperty] private bool _protectPacks;

    partial void OnProtectPacksChanged(bool value) => Remember();

    /// The key packs are signed with, shown so an author can publish it: somebody who knows this
    /// can tell a pack you built from one re-signed by whoever altered it.
    public string AuthorFingerprint
    {
        get
        {
            try
            {
                using var mine = PackAuthor.Mine(SettingsHome);
                return $"your key: {mine.Fingerprint}";
            }
            catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException)
            {
                return $"no key could be made: {e.Message}";
            }
        }
    }

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    /// Shown in the options window, because where a portable tool keeps its state is worth being
    /// able to see rather than having to guess.
    public string SettingsPath => ToolSettings.PathIn(SettingsHome ?? ModStore.DefaultHome());

    /// Somewhere other than beside the executable to keep preferences.
    ///
    /// Only the self-test sets it. That test drives the asset filter, the language and the alpha
    /// toggle, and every one of those is written back the moment it moves — so a run rewrote the
    /// author's settings file as a side effect, and left their tree filter switched off.
    public string? SettingsHome { get; set; }

    /// Which workspace tab is showing: browse, editor, manager.
    [ObservableProperty] private int _workspace;

    public ObservableCollection<WeaponListItem> Weapons { get; } = [];

    /// The skins of the selected weapon, and which of them extraction should also write out.
    ///
    /// One at a time rather than all of them: a weapon carries up to a dozen, each with its own
    /// materials and textures and sometimes a whole model, and an author works on one.
    public ObservableCollection<SkinChoice> SkinChoices { get; } = [];

    [ObservableProperty] private SkinChoice? _chosenSkin;

    /// Whether this weapon has any, so the picker stays out of the way of the ones that do not.
    public bool HasSkins => SkinChoices.Count > 1;

    public string Title => _catalogs is null
        ? "PGAssetTool"
        : $"PGAssetTool — {Weapons.Count} of {_catalogs.Items.Count} weapons";

    public async Task LoadAsync()
    {
        // Preferences are read before the game, so the first catalog load is already in the right
        // language rather than being read once in English and then again.
        // Preferences are read before the game, so the first catalog load is already in the right
        // language rather than being read once in English and then again. Nothing is resolved yet,
        // so the change handlers below find nothing to rebuild.
        _settings = ToolSettings.Load(SettingsHome);
        _loading = true;
        Language = _settings.Language;
        ReplaceableOnly = _settings.ReplaceableOnly;
        OpaqueTextures = _settings.OpaqueTextures;
        Author = _settings.Author;
        Editor.SideBySide = _settings.SideBySide;
        Editor.Linked = _settings.LinkedPreviews;
        Manager.ConfirmChanges = _settings.ConfirmChanges;
        Manager.TileSize = _settings.TileSize;
        ProtectPacks = _settings.ProtectPacks;
        _loading = false;

        try
        {
            await Task.Run(() =>
            {
                _installation = GameInstallation.OpenDetected();
                var game = _installation;
                _bundles = new BundleSet(game);
                _catalogs = GameCatalogs.Load(_bundles, Language);
                _resolver = new WeaponResolver(_bundles, _catalogs);
            });

            Languages.Clear();
            foreach (var (bundle, name) in GameCatalogs.Languages(_bundles!))
                Languages.Add(new LanguageOption(bundle, name));

            // Whatever is being searched for, still. A reload happens under somebody who is in the
            // middle of something — building a pack reloads the game — and putting the whole list
            // back while the search box still says what they typed reads as the search breaking.
            Show(Search.Length == 0
                ? _catalogs!.Items.Weapons
                : _catalogs!.Items.Search(Search, _catalogs.Names));

            Editor.Rescan(WorkspaceRoot);

            // The manager files mods by weapon and by skin, and both are things only the catalogues
            // can name — and name differently in each language. Searching goes the same way: the
            // weapon list's own search, so the manager finds a weapon by any of its names too.
            Manager.Names = Naming;
            Manager.FindWeapons = text => _catalogs is not { } catalogs
                ? null
                : catalogs.Items.Search(text, catalogs.Names).Select(w => w.GameNumber).ToHashSet();

            Manager.Refresh();
            Status = $"{_catalogs.Items.Count} weapons";
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "opening the game");
        }
        finally
        {
            Busy = false;
        }
    }

    /// Re-reads the game from scratch, for after a game update or an external edit.
    public async Task ReloadAsync()
    {
        var wanted = Selected?.Record.GameNumber;

        Editor.Dispose();
        _bundles?.Dispose();
        (_bundles, _catalogs, _resolver, _tree) = (null, null, null, null);
        Detail = null;
        Preview.Clear();
        Busy = true;
        Status = "Reloading…";

        await LoadAsync();

        // Put the reader back where it was, so a reload is not also a loss of place.
        if (wanted is { } number)
            Selected = Weapons.FirstOrDefault(w => w.Record.GameNumber == number);
    }

    partial void OnSearchChanged(string value)
    {
        if (_catalogs is null) return;
        Show(value.Length == 0
            ? _catalogs.Items.Weapons
            : _catalogs.Items.Search(value, _catalogs.Names));
    }

    private void Show(IEnumerable<WeaponRecord> records)
    {
        Weapons.Clear();
        foreach (var record in records)
            Weapons.Add(new WeaponListItem(record, _catalogs!.Localization.Translate(record.LocalizationKey)));
        OnPropertyChanged(nameof(Title));
    }

    async partial void OnSelectedChanged(WeaponListItem? value)
    {
        if (value is null || _resolver is null) { Detail = null; return; }

        Busy = true;
        Status = $"Resolving {value.Name}…";
        try
        {
            // One reader, one weapon at a time: clicking down the list must not have two resolves
            // in the same BundleSet.
            await _reading.WaitAsync();
            try
            {
                var tree = await Task.Run(() => _resolver.Resolve(value.Record));
                Preview.Clear();
                _tree = tree;
                Detail = new WeaponDetailViewModel(tree, ReplaceableOnly) { NodeSelected = ShowPreview };
                Status = $"{value.Name} — {tree.PrefabAssets.Count} objects in {tree.PrefabBundle ?? "no bundle"}";
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "reloading");
            Detail = null;
        }
        finally
        {
            Busy = false;
        }
    }

    /// Loads whatever the clicked node stands for, if it is something that can be looked at.
    ///
    /// Reading goes through the same lock as resolving: one BundleSet, one reader, and clicking
    /// quickly down a tree must not put two decodes into it at once.
    /// Raised when the search box should take focus. The view owns the control; the model only
    /// knows that someone asked.
    public event Action? SearchRequested;

    /// Writes the whole weapon out as a workspace: every replaceable file plus a manifest that
    /// already names each target, so the directory is ready to edit and pack without anything
    /// being wired up by hand.
    [RelayCommand]
    private async Task ExtractWeapon()
    {
        if (_tree is null || _bundles is null) { Status = "Select a weapon first."; return; }

        var tree = _tree;
        await RunExclusively("extracting the weapon", async () =>
        {
            var version = _bundles.Context.HasClassDatabase
                ? GameVersion.Read(_bundles.Context, _bundles.Game)
                : null;

            var skin = ChosenSkin?.Id;
            var export = await Task.Run(() =>
                new WeaponExporter(_bundles) { Opaque = _settings.OpaqueTextures, Skin = skin }
                    .ExportAsWorkspace(tree, WorkspaceRoot, _settings.Author, version));

            LastExport = export.Directory;
            Editor.Rescan(WorkspaceRoot);
            Manager.Refresh();
            Status = $"{export.Assets.Count} files written to {export.Directory}"
                + (export.Skipped.Count > 0 ? $", {export.Skipped.Count} skipped" : "");
        });
    }

    /// Writes out the one object the tree has selected, for when the whole weapon is not wanted.
    [RelayCommand]
    private async Task ExtractSelected()
    {
        if (Detail?.SelectedNode is not { Class: not null, Bundle.Length: > 0 } node)
        {
            Status = "Select an asset in the tree first.";
            return;
        }

        await RunExclusively("extracting the selected asset", async () =>
        {
            var directory = Path.Combine(WorkspaceRoot, "assets");
            var written = await Task.Run(() =>
            {
                if (AssetPreview.Locate(_bundles!, node.Bundle, node.Class.Value, node.PathId, node.Label)
                    is not var (file, info)) return null;

                return new AssetExporter(_bundles!) { Opaque = _settings.OpaqueTextures }
                    .Export(node.Bundle, file, info, directory);
            });

            if (written is null || written.Count == 0) { Status = $"'{node.Label}' could not be written."; return; }

            LastExport = directory;
            Status = $"{string.Join(", ", written.Select(w => Path.GetFileName(w.Path)))} -> {directory}";
            Editor.Rescan(WorkspaceRoot);
            Manager.Refresh();
        });
    }

    /// Runs something that rewrites the game with the reader put down, then opens it again.
    ///
    /// The bundles are held open for browsing and applying rewrites those same files. A separate
    /// process never had to care; a window that browses and installs does.
    ///
    /// Under the same lock as every other use of that reader. Closing it is not enough on its own:
    /// a preview or a workspace comparison already part-way through a bundle keeps that file open
    /// however firmly the reader is disposed underneath it, and the write that follows then fails
    /// with the file in use — intermittently, and only ever on whichever bundle was last looked at.
    private async Task WithReaderClosed(Func<Task> work)
    {
        await _reading.WaitAsync();
        try
        {
            CloseReader();
            try { await work(); }
            finally { await LoadAsync(); }
        }
        finally
        {
            _reading.Release();
        }
    }

    private void CloseReader()
    {
        _bundles?.Dispose();
        (_bundles, _catalogs, _resolver, _tree) = (null, null, null, null);
        Detail = null;
        Preview.Clear();
    }

    /// Builds a pack from each workspace, and installs them when asked.
    ///
    /// Building and applying are one gesture because an author repeats them: edit, pack, apply,
    /// look at it in the game. Building alone stays separate for packs meant to be handed out.
    ///
    /// Several at once install together rather than one after another. Installing is a whole-game
    /// reconcile — restore everything modified, reapply everything enabled — so doing it per pack
    /// repeats that work per pack and passes through states with some of them applied and not the
    /// rest. A pack that will not build does not stop the others; it is reported by name.
    private async Task BuildPack(IReadOnlyList<string> workspaces, bool install)
    {
        await RunExclusively(install ? "building and applying the packs" : "building the packs", async () =>
        {
            var built = new List<PackResult>();
            var refused = new List<string>();

            // Made once for the batch, and only if something in it asks to be signed. Reading the
            // key is cheap; making one the first time is not something to do unasked.
            PackAuthor? signer = null;

            foreach (var workspace in workspaces)
            {
                // Named after the mod, not after the directory it was built in: renaming a
                // working folder should not rename what is about to be handed out.
                var manifest = Core.Pack.Workspace.Read(workspace);
                var output = PackBuilder.OutputFor(workspace, manifest);

                var protect = manifest.Protect ?? ProtectPacks;
                if (protect) signer ??= PackAuthor.Mine(SettingsHome);
                var with = protect ? signer : null;
                try { built.Add(await Task.Run(() => PackBuilder.Build(workspace, output, with))); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    refused.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace))}: {ex.Message}");
                }
            }

            signer?.Dispose();

            var trouble = refused.Count > 0 ? "  " + string.Join("  ", refused) : "";
            Status = $"{built.Count} pack(s), {built.Sum(b => b.Operations)} operation(s), "
                + $"{built.Sum(b => b.Bytes):N0} bytes" + trouble;

            if (!install || built.Count == 0) return;
            await ApplyPacks(built.Select(b => b.Path).ToList(), trouble, $"{built.Count} pack(s) built. ");
        });
    }

    /// Installs packs that came from somewhere other than a workspace — dropped on the window,
    /// most likely, which is how somebody else's mod gets in.
    ///
    /// The same reconcile the rest of the tool installs through, so a pack from outside is subject
    /// to the same backups, the same ledger and the same undo as one built here.
    public Task InstallPacks(IReadOnlyList<string> paths)
        => paths.Count == 0
            ? Task.CompletedTask
            : RunExclusively("installing the packs", () => ApplyPacks(paths, "", ""));

    /// <param name="trouble">What went wrong earlier in the same gesture, to be repeated at the end.</param>
    /// <param name="sofar">What has already happened, for the message that says why nothing more will.</param>
    private async Task ApplyPacks(IReadOnlyList<string> paths, string trouble, string sofar)
    {
        if (_bundles is null) { Status = "The game is not open."; return; }
        var game = _bundles.Game;

        // The manager has always refused to write while the game is running; this path never
        // did, and it is the one an author uses over and over. The packs are built and on disk,
        // so nothing is lost by stopping here — only the writing waits.
        if (GameProcess.IsRunning(game))
        {
            Status = sofar + "The game is running, and rewriting its bundles now can leave a "
                + "half-written file behind — close it, then apply.";
            return;
        }

        CloseReader();

        ReconcileResult result;
        try
        {
            var store = new ModStore(game);
            var version = "unknown";
            using (var reading = new BundleSet(game))
                if (reading.Context.HasClassDatabase) version = GameVersion.Read(reading.Context, game);

            result = await Task.Run(() => new ModApplier(game, store).Install(paths, version));
        }
        finally
        {
            await LoadAsync();
        }

        // The warning is the reason applying from here is worth having: it is the moment an
        // author can still decide that reaching another weapon was not what they meant.
        var shared = result.Shared.Count > 0
            ? "  " + string.Join("  ", result.Shared.Select(s => s.ToString()))
            : "";

        Status = $"{result.Applied.Count} applied, {result.Failed.Count} failed."
            + (result.Failed.Count > 0 ? "  " + string.Join("  ", result.Failed) : "") + shared + trouble;
    }

    /// Where extraction writes. Beside the tool unless the settings say otherwise, because a
    /// window has no meaningful current directory to fall back on.
    /// Somewhere other than the author's workspace to extract into.
    ///
    /// Only the self-test sets it. That test writes, edits, packs and installs for real, and doing
    /// so among directories a person is keeping actual work in means its scratch extracts sit in
    /// the same list as theirs — and, worse, that whichever workspace it happened to select was
    /// sometimes one of theirs.
    public string? WorkspaceOverride { get; set; }

    public string WorkspaceRoot => WorkspaceOverride ?? _settings.WorkspaceIn(SettingsHome ?? ModStore.DefaultHome());

    /// The installation, once it is open. The self-test uses it to put the game back.
    public GameInstallation? Game => _installation;

    /// The last directory written to, so the view can offer to open it.
    [ObservableProperty] private string? _lastExport;

    /// Names what was being done, because a bare exception message says nothing about which of the
    /// several things that can fail here did — "Value cannot be null" on its own is unactionable.
    private static string Describe(Exception ex, string what)
        => $"{ex.Message}  (while {what})";

    /// Long jobs share the one reader and say so while they run.
    private async Task RunExclusively(string what, Func<Task> work)
    {
        Busy = true;
        await _reading.WaitAsync();
        try { await work(); }
        catch (Exception ex) { Status = Describe(ex, what); }
        finally { _reading.Release(); Busy = false; }
    }

    [RelayCommand]
    private void FocusSearch()
    {
        Workspace = BrowseTab;
        SearchRequested?.Invoke();
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync();

    /// Toggled through a command rather than a two-way binding on the menu item. A checkable
    /// MenuItem owns its own IsChecked, and letting it write back means the value the menu happens
    /// to hold when it is first realized wins over the one the model started with.
    [RelayCommand]
    private void ToggleReplaceableOnly() => ReplaceableOnly = !ReplaceableOnly;

    [RelayCommand]
    private void ToggleAlpha() => Preview.ShowAlpha = !Preview.ShowAlpha;

    [RelayCommand]
    private void ShowBrowse() => Workspace = BrowseTab;

    [RelayCommand]
    private void ShowEditor() => Workspace = EditorTab;

    [RelayCommand]
    private void ShowManager() => Workspace = ManagerTab;

    public const int BrowseTab = 0;
    public const int EditorTab = 1;
    public const int ManagerTab = 2;

    /// The pane a key press means, which depends on which workspace is showing.
    public PreviewViewModel? Showing => Workspace switch
    {
        BrowseTab => Preview,
        EditorTab => Editor.Shown,
        _ => null,
    };

    partial void OnOpaqueTexturesChanged(bool value) => Remember();

    /// Re-reads the workspace directory. Files there are edited by other programs, and a watcher
    /// does miss things, so asking outright stays available.
    [RelayCommand]
    private void RescanWorkspaces() => Editor.Rescan(WorkspaceRoot);

    // Typed rather than picked, so it lands here on every keystroke. The file is a few hundred
    // bytes and writing it costs nothing worth debouncing for.
    partial void OnAuthorChanged(string value) => Remember();

    partial void OnReplaceableOnlyChanged(bool value)
    {
        Remember();
        if (_tree is null) return;

        Preview.Clear();
        Detail = new WeaponDetailViewModel(_tree, value) { NodeSelected = ShowPreview };
    }

    /// Changing the language means every name in the catalogs, so the game is read again.
    /// Only the translation table depends on the language. The items, the lookup table, the skins
    /// and the search index do not, so changing which name is displayed re-reads one small bundle
    /// rather than the whole game — it used to throw the reader away and start over.
    partial void OnLanguageChanged(string value)
    {
        Remember();
        if (_loading || _bundles is null || _catalogs is null) return;

        try
        {
            _catalogs = _catalogs.WithLanguage(_bundles, value);
            _resolver = new WeaponResolver(_bundles, _catalogs);
            Show(Search.Length == 0 ? _catalogs.Items.Weapons : _catalogs.Items.Search(Search, _catalogs.Names));

            // The manager's headings are weapon and skin names too, and they are read out of the
            // table that has just changed rather than out of what a pack was called when it was
            // built — so this is where they follow.
            Manager.Relabel();
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "changing the language");
        }
    }

    /// What a pack's weapon and skin are called in the language the catalogues are open in.
    ///
    /// Looked up by number and by skin id, not by anything the pack carries: the pack was built in
    /// whatever language its author was using, and packs built before this existed carry nothing to
    /// translate at all. What it recorded is the last resort — better than a blank, and the honest
    /// answer for a weapon this installation does not have.
    private (string? Name, string? Variant) Naming(Core.Pack.PackSubject subject)
    {
        if (_catalogs is not { } catalogs) return (null, null);

        // Only weapons can be looked up, because weapons are the only kind anything here reads a
        // catalogue for. Another kind falls through to the keys the pack recorded, which is the
        // right answer until there is a catalogue to ask.
        var record = subject.Kind == Core.Pack.PackKind.Weapon
            ? catalogs.Items.ByGameNumber(subject.Number)
            : null;

        var variant = record is null || subject.Variant.Length == 0
            ? null
            : catalogs.Skins.ForWeapon(record.Index).FirstOrDefault(s =>
                string.Equals(s.Id, subject.Variant, StringComparison.OrdinalIgnoreCase));

        return (Say(record?.LocalizationKey ?? subject.NameKey),
                Say(variant?.LocalizationKey ?? subject.VariantKey));

        string? Say(string? key)
            => key is { Length: > 0 } ? catalogs.Localization.Translate(key) : null;
    }

    private void Remember()
    {
        if (_loading) return;
        _settings = _settings with
        {
            Language = Language, ReplaceableOnly = ReplaceableOnly, OpaqueTextures = OpaqueTextures,
            Author = Author.Trim(),
            SideBySide = Editor.SideBySide, LinkedPreviews = Editor.Linked,
            ConfirmChanges = Manager.ConfirmChanges,
            TileSize = Manager.TileSize, ProtectPacks = ProtectPacks,
        };
        try { _settings.Save(SettingsHome); }
        catch (IOException) { }
    }

    /// Decodes the textures the renderer drawing this mesh uses, one per submesh.
    ///
    /// A Mesh asset carries no appearance of its own, so without this a weapon previews as grey
    /// geometry and the thing being judged — how a texture sits on the model — is invisible.
    private IReadOnlyList<PreviewImage?>? TexturesFor(long meshPathId)
    {
        if (_tree?.MeshTextures.FirstOrDefault(m => m.MeshPathId == meshPathId) is not { } slots) return null;

        return slots.BySubMesh.Select(node =>
        {
            if (node is null) return null;
            try
            {
                if (AssetPreview.Locate(_bundles!, node.Bundle, AssetClassID.Texture2D, node.PathId, node.Name)
                    is not var (file, info)) return null;

                var field = _bundles!.Context.Deserialize(file, info);
                return field is null ? null : AssetPreview.Texture(_bundles, node.Bundle, field);
            }
            catch (Exception)
            {
                // A preview is never worth failing a selection over.
                return null;
            }
        }).ToList();
    }

    partial void OnDetailChanged(WeaponDetailViewModel? value)
    {
        OfferTextures();
        OfferSkins(value?.Tree);
    }

    /// The skins this weapon has, offered so one of them can be extracted alongside its own files.
    private void OfferSkins(PGAssetTool.Core.Weapons.WeaponTree? tree)
    {
        SkinChoices.Clear();
        SkinChoices.Add(SkinChoice.None);
        foreach (var skin in tree?.Skins ?? [])
            SkinChoices.Add(new SkinChoice(
                skin.Record.Id, skin.DisplayName ?? skin.Record.Id, skin.Model is not null));

        // Deliberately not remembered across weapons: "the Christmas one" is not a thing another
        // weapon has, and silently extracting a skin nobody asked for is worse than asking again.
        ChosenSkin = SkinChoices[0];
        OnPropertyChanged(nameof(HasSkins));
    }

    /// A chosen texture is loaded when it is first picked, not when the list is built: a weapon
    /// offers a dozen or more and almost none of them will be looked at.
    private async void OnPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewViewModel.ChosenTexture)) return;
        if (Preview.ChosenTexture is not { PathId: not 0 } choice) return;

        try
        {
            await _reading.WaitAsync();
            try
            {
                var loaded = await Task.Run(() =>
                {
                    if (AssetPreview.Locate(_bundles!, choice.Bundle, AssetClassID.Texture2D, choice.PathId, choice.Name)
                        is not var (file, info)) return null;
                    var field = _bundles!.Context.Deserialize(file, info);
                    return field is null ? null : AssetPreview.Texture(_bundles, choice.Bundle, field);
                });

                // Only if the choice still stands: reading a texture takes long enough for someone
                // to have moved on to another one.
                if (loaded is not null && Preview.ChosenTexture == choice) Preview.Wear(loaded);
            }
            finally { _reading.Release(); }
        }
        catch (Exception ex) { Status = Describe(ex, "loading a texture for the preview"); }
    }

    /// Every texture the weapon reaches, offered so a skin can be tried on a mesh by hand.
    private void OfferTextures()
    {
        Preview.TextureChoices.Clear();
        Preview.TextureChoices.Add(new TextureChoice("(automatic)", "", 0));

        if (Detail is null) return;

        var seen = new HashSet<(string, long)>();
        foreach (var node in AllNodes(Detail.Roots))
        {
            if (node.Class != AssetClassID.Texture2D || node.Bundle.Length == 0) continue;
            if (!seen.Add((node.Bundle, node.PathId))) continue;
            Preview.TextureChoices.Add(new TextureChoice(node.Label, node.Bundle, node.PathId));
        }
    }

    private static IEnumerable<TreeNode> AllNodes(IEnumerable<TreeNode> nodes)
        => nodes.SelectMany(n => new[] { n }.Concat(AllNodes(n.Children)));

    private async void ShowPreview(TreeNode? node)
    {
        if (node?.Class is not (AssetClassID.Texture2D or AssetClassID.Mesh or AssetClassID.AudioClip))
        {
            Preview.Clear(node?.Class is null ? null : $"No preview for {node.Class}.");
            return;
        }

        if (node.Bundle.Length == 0) { Preview.Clear("This object's container is not known."); return; }

        try
        {
            // Reading goes through the same lock as resolving: one BundleSet, one reader, and
            // clicking quickly down a tree must not put two decodes into it at once.
            await _reading.WaitAsync();
            try
            {
                var loaded = await Task.Run(object? () =>
                {
                    // An icon is registered by name with no path id, and may live in the game's own
                    // resources.assets rather than in a bundle, so finding it is not a lookup by id.
                    if (AssetPreview.Locate(_bundles!, node.Bundle, node.Class.Value, node.PathId, node.Label)
                        is not var (file, info)) return null;

                    var field = _bundles!.Context.Deserialize(file, info);
                    if (field is null) return null;

                    return node.Class switch
                    {
                        AssetClassID.Texture2D => AssetPreview.Texture(_bundles, node.Bundle, field),
                        AssetClassID.AudioClip => AssetPreview.Audio(_bundles, node.Bundle, field),
                        _ => AssetPreview.Mesh(field),
                    };
                });

                switch (loaded)
                {
                    case PreviewImage picture:
                        Preview.Show(picture, $"{node.Label}   @ {node.Bundle}", node.AlphaIsCoverage);
                        break;
                    case UnityMesh mesh:
                        Preview.Show(mesh, $"{node.Label}   @ {node.Bundle}", TexturesFor(node.PathId),
                            subject: $"{node.Bundle}:{node.PathId}");
                        break;
                    case PreviewSound sound:
                        Preview.Show(sound, $"{node.Label}   @ {node.Bundle}");
                        break;
                    default:
                        Preview.Clear(_bundles!.Context.HasClassDatabase || !node.Bundle.Contains('.')
                            ? $"'{node.Label}' could not be read from {node.Bundle}."
                            : $"'{node.Label}' lives in {node.Bundle}, which needs {ClassPackage.FileName}.");
                        break;
                }
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Preview.Clear(ex.Message);
        }
    }

    public void Dispose()
    {
        Editor.Dispose();
        _bundles?.Dispose();
        _reading.Dispose();
    }
}

public sealed record WeaponListItem(WeaponRecord Record, string? Translated)
{
    public string Name => Translated ?? Record.Slug;
    public string Number => $"#{Record.GameNumber}";

    /// Shown next to the name because the two numbering systems disagree for all but six weapons,
    /// and the prefab number is the one that appears in asset paths.
    public string Prefab => Record.PrefabName;
}

/// One of the game's own translation tables, named in its own script.
public sealed record LanguageOption(string Bundle, string Name)
{
    public override string ToString() => Name;
}

/// A skin offered for extraction. The null id means the weapon on its own.
public sealed record SkinChoice(string? Id, string Name, bool HasModel)
{
    public static SkinChoice None { get; } = new(null, "(no skin)", false);

    public override string ToString() => HasModel ? $"{Name}  — its own model" : Name;
}
