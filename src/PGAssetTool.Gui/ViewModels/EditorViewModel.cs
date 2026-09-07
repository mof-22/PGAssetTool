using System.Collections.ObjectModel;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Preview;

namespace PGAssetTool.Gui.ViewModels;

/// One extracted weapon, as a row in the list of workspaces.
public sealed record WorkspaceItem(string Directory, string Name, int Files, int Edited)
{
    public string Summary => Edited == 0 ? $"{Files} files" : $"{Files} files, {Edited} edited";
}

/// The workspace side: what has been extracted, what has been edited in it, and what the edits look
/// like against the originals.
///
/// The files are edited by other programs — an image editor, Blender — so nothing here is
/// authoritative for long. The directory is watched and re-read rather than kept in step by hand.
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly Func<BundleSet?> _bundles;
    private readonly SemaphoreSlim _reading;
    private FileSystemWatcher? _watcher;
    private Timer? _settle;

    public EditorViewModel(Func<BundleSet?> bundles, SemaphoreSlim reading, AlphaPreference? alpha = null)
    {
        (_bundles, _reading) = (bundles, reading);
        alpha ??= new AlphaPreference();
        Original = new PreviewViewModel(alpha);
        Edited = new PreviewViewModel(alpha);

        foreach (var preview in new[] { Original, Edited })
        {
            var pane = preview;
            var other = ReferenceEquals(pane, Original) ? Edited : Original;
            pane.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PreviewViewModel.ChosenTexture):
                        if (pane.ChosenTexture is { PathId: -1 } choice) WearFromDisk(pane, choice);
                        if (Linked && !_pairing) other.ChosenTexture = pane.ChosenTexture;
                        break;

                    // Both panes carry the same value, so the mirroring settles at once: the other
                    // pane's own notification finds nothing to change and stops there.
                    case nameof(PreviewViewModel.Camera):
                        if (Linked && !_pairing) other.Camera = pane.Camera;
                        break;

                    // Alpha for the same reason as the rest: a comparison where one half honours
                    // the channel and the other does not shows a difference that is not the mod.
                    case nameof(PreviewViewModel.ShowAlpha):
                        if (Linked && !_pairing) other.ShowAlpha = pane.ShowAlpha;
                        break;
                }
            };
        }
    }

    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];
    public ObservableCollection<WorkspaceFile> Files { get; } = [];

    [ObservableProperty] private string _root = "";
    [ObservableProperty] private WorkspaceItem? _selectedWorkspace;
    [ObservableProperty] private WorkspaceFile? _selectedFile;
    [ObservableProperty] private string _status = "";

    /// The original as the game holds it, and the file as it stands now.
    public PreviewViewModel Original { get; }
    public PreviewViewModel Edited { get; }

    [ObservableProperty] private bool _sideBySide;

    /// Whether the two halves are turned and dressed together.
    ///
    /// On by default, because the question a comparison answers is what changed — and two models at
    /// two angles wearing two textures differ in three ways at once, only one of which is the mod.
    /// It comes off for the case it cannot serve: a replaced mesh, where the two are different
    /// models and looking at each on its own terms is the point.
    [ObservableProperty] private bool _linked = true;

    /// Set while both halves are being filled, so neither hands its view to the other mid-way.
    private bool _pairing;

    /// Brings the halves together at the moment linking is asked for, rather than leaving them
    /// apart until something is turned. The one on show leads: it is the one just been looked at.
    partial void OnLinkedChanged(bool value)
    {
        if (value) Pair();
    }

    private void Pair()
    {
        if (!Linked) return;

        var (from, to) = ShowingEdited ? (Edited, Original) : (Original, Edited);
        to.Camera = from.Camera;
        to.ChosenTexture = from.ChosenTexture;
    }

    /// Which of the two a single-pane comparison is showing. Flicking between them in place is
    /// better at exposing a small difference than putting them next to each other.
    [ObservableProperty] private bool _showingEdited = true;

    public PreviewViewModel Shown => ShowingEdited ? Edited : Original;

    partial void OnShowingEditedChanged(bool value) => OnPropertyChanged(nameof(Shown));

    [RelayCommand]
    private void Flip() => ShowingEdited = !ShowingEdited;

    /// Asked to build packs, and to install them as well when the caller wants the round trip.
    /// The shell owns the game, so the editor only says what it wants done.
    public event Func<IReadOnlyList<string>, bool, Task>? PackRequested;

    [RelayCommand]
    private Task Pack() => Build(install: false);

    [RelayCommand]
    private Task PackAndApply() => Build(install: true);

    private async Task Build(bool install)
    {
        var chosen = Chosen;
        if (chosen.Count == 0) { Status = "Nothing selected."; return; }
        if (PackRequested is null) return;

        await PackRequested(chosen.Select(w => w.Directory).ToList(), install);
        Refresh();
    }

    /// The workspaces a command acts on: everything highlighted, or the one current row.
    public IReadOnlyList<WorkspaceItem> Chosen =>
        Selection.Count > 0 ? Selection.ToList()
        : SelectedWorkspace is { } one ? [one]
        : [];

    /// Bound to the list's own selection. SelectedWorkspace stays the anchor the file list and
    /// the details form follow, because those only make sense for one workspace at a time.
    public ObservableCollection<WorkspaceItem> Selection { get; } = [];

    public bool CanPack => SelectedWorkspace is not null;

    /// What the icon list calls having no icon. A real path can never be this.
    private const string None = "(none)";

    /// The manifest's descriptive half, as a form.
    ///
    /// Held apart from the manifest on disk rather than written through on every keystroke: a
    /// half-typed version string is not something to save, and a workspace is a directory other
    /// programs are watching. Nothing moves until Apply.
    [ObservableProperty] private string _folderName = "";
    [ObservableProperty] private string _packName = "";
    [ObservableProperty] private string _packAuthor = "";
    [ObservableProperty] private string _packVersion = "";
    [ObservableProperty] private string _packDescription = "";

    /// The id is what the ledger keys an installed mod by. Changing it turns an update into a
    /// second, separate install of the same mod, so it is shown and not edited.
    [ObservableProperty] private string _packId = "";

    /// What the pack will be built as, so the name field can say so while it is being typed.
    public string PackFileName => PackBuilder.FileNameFor(new PackManifest
    {
        Id = PackId, Name = PackName ?? "",
    });

    partial void OnPackNameChanged(string value) => OnPropertyChanged(nameof(PackFileName));

    /// The picture the pack shows itself with, and the images in the workspace to choose from.
    public ObservableCollection<string> IconChoices { get; } = [];

    [ObservableProperty] private string? _iconFile;

    /// Whether this pack is signed and scrambled when built. Null means whatever the setting says.
    [ObservableProperty] private bool? _protect;

    private void ShowDetails(WorkspaceItem? workspace)
    {
        var manifest = workspace is null ? null : Details(workspace.Directory);

        IconChoices.Clear();
        IconChoices.Add(None);
        if (workspace is not null)
            foreach (var picture in Workspace.Pictures(workspace.Directory)) IconChoices.Add(picture);

        // Whatever the manifest names, if it is still there. A pack that claims a picture it no
        // longer carries is worse than one with none.
        IconFile = manifest?.Icon is { Length: > 0 } named && IconChoices.Contains(named) ? named : None;
        Protect = manifest?.Protect;

        FolderName = workspace?.Name ?? "";
        PackId = manifest?.Id ?? "";
        PackName = manifest?.Name ?? "";
        PackAuthor = manifest?.Author ?? "";
        PackVersion = manifest?.Version ?? "";
        PackDescription = manifest?.Description ?? "";
    }

    private static PackManifest? Details(string directory)
    {
        try { return WorkspaceView.Open(directory)?.Manifest; }
        catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
    }

    /// Writes the edited manifest and, if the folder was renamed too, moves the directory.
    ///
    /// The rename happens last: the manifest is written into the workspace, and writing it after a
    /// move would mean knowing which of the two paths to write to when the move half-failed.
    [RelayCommand]
    private void ApplyDetails()
    {
        if (SelectedWorkspace is not { } workspace) { Status = "Nothing selected."; return; }

        try
        {
            var manifest = Workspace.Read(workspace.Directory) with
            {
                Name = PackName.Trim(),
                Author = PackAuthor.Trim(),
                Version = PackVersion.Trim(),
                Description = PackDescription.Trim(),
                Icon = IconFile is null || IconFile == None ? "" : IconFile,
                Protect = Protect,
            };
            Workspace.Save(workspace.Directory, manifest);

            // The watcher holds a handle on the directory it is watching, and Windows will not
            // rename a directory out from under one. Rescan puts a watcher back on wherever it ends.
            Watch(null);

            var was = Path.GetFullPath(Path.TrimEndingDirectorySeparator(workspace.Directory));
            var moved = Workspace.Rename(was, FolderName);
            var renamed = !string.Equals(moved, was, StringComparison.Ordinal);

            // The watcher is still pointed at the old path, and rescanning from a moved directory
            // finds nothing under the name it was selected by.
            Rescan(Root);
            SelectedWorkspace = Workspaces.FirstOrDefault(w =>
                string.Equals(Path.GetFullPath(w.Directory), moved, StringComparison.OrdinalIgnoreCase));

            Status = renamed
                ? $"Saved, and the folder is now '{Path.GetFileName(moved)}'."
                : "Saved.";
        }
        catch (Exception ex)
        {
            Status = $"{ex.Message}  (while saving the pack details)";
        }
    }

    /// Puts the form back to what is on disk, for after a change nobody wants to keep.
    [RelayCommand]
    private void RevertDetails() => ShowDetails(SelectedWorkspace);

    /// What is about to be deleted, while it is being asked about; null the rest of the time.
    [ObservableProperty] private Confirmation? _asking;

    [RelayCommand]
    private void Dismiss() => Asking = null;

    [RelayCommand]
    private async Task Proceed()
    {
        if (Asking is { } asking) await asking.Proceed();
        Asking = null;
    }

    /// Throws a workspace away, folder and all.
    ///
    /// A workspace is a directory this tool wrote, and until now the only way to be rid of one was
    /// to leave the tool and find it on disk — which a finished mod and a folder full of test
    /// extracts both eventually need. It acts on the same selection everything else in this pane
    /// acts on, and it asks first: what goes is the author's own work as much as the tool's, since
    /// a mod half-built lives in exactly the directory the extract began as.
    [RelayCommand]
    private void Delete()
    {
        var chosen = Chosen;
        if (chosen.Count == 0) { Status = "Nothing selected."; return; }

        var subject = chosen.Count == 1 ? $"'{chosen[0].Name}'" : $"{chosen.Count} workspaces";
        Asking = new Confirmation(
            $"Delete {subject}?",
            (chosen.Count > 1 ? string.Join(", ", chosen.Select(w => w.Name)) + "\n\n" : "")
            + "The folder goes with everything in it, edits included, and this is not something the "
            + "tool can undo. Packs already built from it are files of their own and stay where "
            + "they are, installed or not.",
            () => { Discard(chosen); return Task.CompletedTask; });
    }

    private void Discard(IReadOnlyList<WorkspaceItem> chosen)
    {
        // The watcher holds a handle on the directory it watches, and Windows will not delete a
        // directory out from under one. Rescan puts a watcher back on whatever is selected after.
        Watch(null);

        var (gone, failed) = (0, new List<string>());
        foreach (var workspace in chosen)
        {
            try
            {
                // Never anywhere but the folder this pane is showing. The list is built by walking
                // that folder, so nothing else can get in — but a delete is worth checking twice,
                // and a workspace whose row is stale would otherwise be a path from anywhere.
                if (!Inside(Root, workspace.Directory))
                {
                    failed.Add($"{workspace.Name} is not in {Root}");
                    continue;
                }

                Directory.Delete(workspace.Directory, recursive: true);
                gone++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{workspace.Name}: {ex.Message}");
            }
        }

        Rescan(Root);
        Status = failed.Count == 0
            ? $"{gone} deleted."
            : $"{gone} deleted, {failed.Count} not — {string.Join("; ", failed)}";
    }

    private static bool Inside(string root, string path)
    {
        if (root.Length == 0) return false;

        var within = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(within, StringComparison.OrdinalIgnoreCase);
    }

    /// Writes a drawing of the model, as the preview is showing it, and makes it the pack's picture.
    ///
    /// The one the export draws is of the vanilla weapon, since nothing has been edited at that
    /// point. This is the mod — the author's own mesh wearing their own texture, from the angle
    /// they turned it to.
    public void CaptureIcon(PreviewImage picture)
    {
        if (SelectedWorkspace is not { } workspace) { Status = "Nothing selected."; return; }

        if (Core.Preview.PackIcon.IsBlank(picture))
        {
            Status = "There is nothing in the frame to make an icon out of.";
            return;
        }

        try
        {
            Core.Preview.PackIcon.Write(picture, Path.Combine(workspace.Directory, Core.Preview.PackIcon.FileName));

            var manifest = Workspace.Read(workspace.Directory) with { Icon = Core.Preview.PackIcon.FileName };
            Workspace.Save(workspace.Directory, manifest);

            ShowDetails(workspace);
            Status = $"The pack now shows this view of the model, {picture.Width}×{picture.Height}.";
        }
        catch (Exception ex)
        {
            Status = $"{ex.Message}  (while saving the icon)";
        }
    }

    public void Rescan(string root)
    {
        Root = root;
        var chosen = SelectedWorkspace?.Directory;

        // The rows are about to be replaced by new instances, so anything held here refers to
        // workspaces that no longer exist as far as the list is concerned.
        Selection.Clear();
        Workspaces.Clear();
        foreach (var directory in WorkspaceView.Discover(root))
        {
            if (WorkspaceView.Open(directory) is not { } view) continue;
            Workspaces.Add(new WorkspaceItem(directory, view.Name, view.Files.Count, view.EditedCount));
        }

        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Directory == chosen) ?? Workspaces.FirstOrDefault();
        OnPropertyChanged(nameof(CanPack));
        Status = Workspaces.Count == 0
            ? $"Nothing extracted yet. Weapons written from Browse land in {root}."
            : $"{Workspaces.Count} extracted";
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceItem? value)
    {
        ShowDetails(value);
        var chosen = SelectedFile?.RelativePath;

        Files.Clear();
        if (value is not null && WorkspaceView.Open(value.Directory) is { } view)
            foreach (var file in view.Files) Files.Add(file);

        Watch(value?.Directory);
        SelectedFile = Files.FirstOrDefault(f => f.RelativePath == chosen)
            ?? Files.FirstOrDefault(f => f.Edited)
            ?? Files.FirstOrDefault();
    }

    async partial void OnSelectedFileChanged(WorkspaceFile? value)
    {
        Original.Clear("Nothing selected.");
        Edited.Clear("Nothing selected.");
        if (value is null) return;

        await CompareAsync(value);
    }

    /// Loads both sides of one file.
    ///
    /// The edited side goes through the same decoders the importers use, so a file that could not
    /// be packed shows as unreadable here rather than as a preview of something the pack would
    /// never contain.
    private async Task CompareAsync(WorkspaceFile file)
    {
        try
        {
            await _reading.WaitAsync();
            try
            {
                // Asked for inside the lock, not outside it. A file watcher fires this off on its
                // own thread, and reading the reader first meant holding one that the pack-and-apply
                // waiting on the same lock was about to put down — so the comparison ran against a
                // disposed reader, or kept the bundle open across a write to it.
                if (_bundles() is not { } bundles) { Edited.Clear("The game is not open."); return; }

                var loaded = await Task.Run(() => (
                    Game: FromGame(bundles, file),
                    Disk: AssetPreview.FromFile(file.FullPath)));

                // The link is off while both halves are being filled, and put back afterwards.
                //
                // The two are loaded one after the other, so for a moment one holds the file being
                // opened and the other still holds the one before it. Mirroring across that moment
                // wrote the new file's angle into the old file's memory — and since each pane
                // remembers per model, going back and forth between two meshes walked the wrong
                // view from one to the other and back, coming round again every few passes.
                _pairing = true;
                try
                {
                    ShowIn(Original, loaded.Game, $"{file.Name} in the game", file, "game");
                    ShowIn(Edited, loaded.Disk,
                        file.Edited ? $"{file.Name} as edited" : $"{file.Name} unchanged", file, "disk");
                }
                finally
                {
                    _pairing = false;
                }

                Pair();
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Edited.Clear($"{ex.Message}  (while comparing against the game)");
        }
    }

    private static object? FromGame(BundleSet bundles, WorkspaceFile file)
    {
        if (!Enum.TryParse<AssetClassID>(file.Target.Class, out var cls)) return null;
        if (AssetPreview.Locate(bundles, file.Target.Container, cls, file.Target.PathId ?? 0, file.Target.Name)
            is not var (opened, info)) return null;

        var field = bundles.Context.Deserialize(opened, info);
        if (field is null) return null;

        return cls switch
        {
            AssetClassID.Texture2D => AssetPreview.Texture(bundles, file.Target.Container, field),
            AssetClassID.Mesh => AssetPreview.Mesh(field),
            AssetClassID.AudioClip => AssetPreview.Audio(bundles, file.Target.Container, field),
            _ => null,
        };
    }

    /// <param name="side">
    /// Which half of the comparison this is. Part of the subject because the two panes hold two
    /// readings of one file, and a pane must not take the other's view for its own.
    /// </param>
    private void ShowIn(
        PreviewViewModel preview, object? loaded, string caption, WorkspaceFile file, string side)
    {
        switch (loaded)
        {
            case PreviewImage picture: preview.Show(picture, caption, file.AlphaIsCoverage); break;

            case UnityMesh mesh:
                // Read after showing, not before: showing is what looks up the view this model was
                // last left at, and the texture that comes back with it may not be the one the
                // pane happened to be wearing a moment ago.
                preview.Show(mesh, caption, null, $"{side}:{file.RelativePath}");
                Offer(preview, preview.ChosenTexture?.Name);
                break;

            case PreviewSound sound: preview.Show(sound, caption); break;
            default: preview.Clear("Nothing to show for this one."); break;
        }
    }

    /// Offers the workspace's own images to put on a mesh.
    ///
    /// Which texture belongs on which part is decided by the renderer in the game's prefab, which
    /// the editor never resolves — so a mesh here draws grey unless somebody says what to put on
    /// it. Offering the author's own files is both the useful answer and the honest one: what they
    /// want to see is their mesh wearing their texture, which is what the mod is.
    /// <param name="wearing">
    /// The one to put back on, when the same model is being read again. Emptying the list empties
    /// the box bound to it, which writes a null back through the selection, so a choice that is to
    /// survive has to be made again — and the file behind it may well be what changed, which is why
    /// it is read from disk rather than assumed to be still on the model.
    /// </param>
    private void Offer(PreviewViewModel preview, string? wearing = null)
    {
        preview.TextureChoices.Clear();
        if (SelectedWorkspace is not { } workspace) return;

        preview.TextureChoices.Add(new TextureChoice("(none)", "", 0));
        foreach (var picture in Workspace.Pictures(workspace.Directory))
            preview.TextureChoices.Add(new TextureChoice(picture, workspace.Directory, -1));

        if (wearing is null) return;
        if (preview.TextureChoices.FirstOrDefault(c => c.Name == wearing) is not { } again) return;

        preview.ChosenTexture = again;
        WearFromDisk(preview, again);
    }

    /// Reads one of those images off disk and puts it on the model.
    ///
    /// A path id of -1 marks a choice that lives in the workspace rather than in the game, which is
    /// the only kind the editor offers.
    private void WearFromDisk(PreviewViewModel preview, TextureChoice choice)
    {
        try
        {
            var path = Path.Combine(choice.Bundle, choice.Name);
            if (AssetPreview.FromFile(path) is PreviewImage picture) preview.Wear(picture);
            else Status = $"'{choice.Name}' could not be read as an image.";
        }
        catch (Exception ex)
        {
            Status = $"{ex.Message}  (while putting a texture on the model)";
        }
    }

    /// Takes files dropped on the editor into the workspace that is selected.
    ///
    /// An author's other tools write wherever they write, and the step between them and here was
    /// finding the workspace on disk and copying the file over by hand. What a file is meant to
    /// replace is nearly always written on it — an edited `ultimatum.png` is the workspace's
    /// `ultimatum.png` — so the name decides, and the file in front of them is the fallback for
    /// when an editor has saved under a name of its own.
    public void Import(IReadOnlyList<string> paths)
    {
        if (SelectedWorkspace is not { } workspace)
        {
            Status = "Choose a workspace first, and these will go into it.";
            return;
        }

        var (placed, refused) = (new List<string>(), new List<string>());
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) { refused.Add($"{Path.GetFileName(path)}: not a file"); continue; }

                if (Destination(path) is not { } destination)
                {
                    refused.Add($"{Path.GetFileName(path)}: nothing here it could replace");
                    continue;
                }

                placed.Add(Place(workspace.Directory, destination, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refused.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Refresh();
        Status = (placed.Count > 0 ? $"Put in place: {string.Join(", ", placed)}." : "")
            + (refused.Count > 0 ? $" {refused.Count} not taken — {string.Join("; ", refused)}" : "");
    }

    /// The file in the workspace a dropped one is meant to become.
    private WorkspaceFile? Destination(string path)
    {
        var format = Path.GetExtension(path).TrimStart('.');
        if (Replaceable.OperationForFormat(format) is not { } operation) return null;

        var dropped = Path.GetFileName(path);
        if (Files.FirstOrDefault(f => string.Equals(f.Name, dropped, StringComparison.OrdinalIgnoreCase))
            is { } exact)
            return exact;

        // The same name in another format the same operation takes — a WAV where an Ogg was
        // written out. Only when one file answers to it: two would be a guess, and a guess here
        // overwrites something.
        var stem = Path.GetFileNameWithoutExtension(path);
        var alike = Files
            .Where(f => f.Operation == operation
                && string.Equals(Path.GetFileNameWithoutExtension(f.Name), stem, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (alike.Count > 0) return alike.Count == 1 ? alike[0] : null;

        // Nothing by that name: the one being looked at, if it is the same kind of thing. An
        // image editor saving as 'export.png' is the case, and the file on show is the answer to
        // "which one did you mean" that the person has already given by selecting it.
        return SelectedFile is { } selected && selected.Operation == operation ? selected : null;
    }

    /// Writes the dropped file in, and answers what it landed as.
    private static string Place(string directory, WorkspaceFile destination, string path)
    {
        var dropped = Path.GetExtension(path);
        if (string.Equals(dropped, Path.GetExtension(destination.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(path, destination.FullPath, overwrite: true);
            return destination.Name;
        }

        // Another format of the same kind. It goes in under its own extension and the manifest is
        // pointed at it, rather than leaving Ogg bytes in a file called .wav: the importer reads
        // the bytes and would not care, but everything a person reads afterwards would be lying.
        var renamed = Path.ChangeExtension(destination.RelativePath, dropped);
        File.Copy(path, Path.Combine(directory, renamed.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);

        var manifest = Workspace.Read(directory);
        Workspace.Save(directory, manifest with
        {
            Operations = manifest.Operations
                .Select(o => string.Equals(o.Source, destination.RelativePath, StringComparison.OrdinalIgnoreCase)
                    ? o with { Source = renamed }
                    : o)
                .ToList(),
        });

        if (File.Exists(destination.FullPath)) File.Delete(destination.FullPath);
        return renamed;
    }

    /// Watches the workspace so an edit made elsewhere shows up without being asked for.
    ///
    /// A save is rarely one event — editors write, rename and touch — so the reaction is delayed
    /// until the writing stops. Manual rescanning stays available because watchers do miss things.
    private void Watch(string? directory)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (directory is null || !Directory.Exists(directory)) return;

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += (_, _) => Settle();
        _watcher.Created += (_, _) => Settle();
        _watcher.Renamed += (_, _) => Settle();
        _watcher.Deleted += (_, _) => Settle();
    }

    /// Raised when the watched directory has stopped changing. The view marshals it onto the UI
    /// thread; a watcher fires on its own.
    public event Action? Settled;

    private void Settle()
    {
        _settle?.Dispose();
        _settle = new Timer(_ => Settled?.Invoke(), null, 400, Timeout.Infinite);
    }

    public void Refresh()
    {
        var file = SelectedFile?.RelativePath;
        Rescan(Root);
        if (file is not null && Files.FirstOrDefault(f => f.RelativePath == file) is { } again)
            SelectedFile = again;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _settle?.Dispose();
    }
}
