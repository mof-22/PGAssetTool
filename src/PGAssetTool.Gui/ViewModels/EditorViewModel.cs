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

    public EditorViewModel(Func<BundleSet?> bundles, SemaphoreSlim reading)
        => (_bundles, _reading) = (bundles, reading);

    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];
    public ObservableCollection<WorkspaceFile> Files { get; } = [];

    [ObservableProperty] private string _root = "";
    [ObservableProperty] private WorkspaceItem? _selectedWorkspace;
    [ObservableProperty] private WorkspaceFile? _selectedFile;
    [ObservableProperty] private string _status = "";

    /// The original as the game holds it, and the file as it stands now.
    public PreviewViewModel Original { get; } = new();
    public PreviewViewModel Edited { get; } = new();

    [ObservableProperty] private bool _sideBySide;

    /// Which of the two a single-pane comparison is showing. Flicking between them in place is
    /// better at exposing a small difference than putting them next to each other.
    [ObservableProperty] private bool _showingEdited = true;

    public PreviewViewModel Shown => ShowingEdited ? Edited : Original;

    partial void OnShowingEditedChanged(bool value) => OnPropertyChanged(nameof(Shown));

    [RelayCommand]
    private void Flip() => ShowingEdited = !ShowingEdited;

    /// Asked to build a pack, and to install it as well when the caller wants the round trip.
    /// The shell owns the game, so the editor only says what it wants done.
    public event Func<string, bool, Task>? PackRequested;

    [RelayCommand]
    private Task Pack() => Build(install: false);

    [RelayCommand]
    private Task PackAndApply() => Build(install: true);

    private async Task Build(bool install)
    {
        if (SelectedWorkspace is not { } workspace) { Status = "Nothing selected."; return; }
        if (PackRequested is null) return;

        await PackRequested(workspace.Directory, install);
        Refresh();
    }

    public bool CanPack => SelectedWorkspace is not null;

    public void Rescan(string root)
    {
        Root = root;
        var chosen = SelectedWorkspace?.Directory;

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
        if (_bundles() is not { } bundles) { Edited.Clear("The game is not open."); return; }

        try
        {
            await _reading.WaitAsync();
            try
            {
                var loaded = await Task.Run(() => (
                    Game: FromGame(bundles, file),
                    Disk: AssetPreview.FromFile(file.FullPath)));

                ShowIn(Original, loaded.Game, $"{file.Name} in the game");
                ShowIn(Edited, loaded.Disk, file.Edited ? $"{file.Name} as edited" : $"{file.Name} unchanged");
            }
            finally
            {
                _reading.Release();
            }
        }
        catch (Exception ex)
        {
            Edited.Clear(ex.Message);
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
            _ => null,
        };
    }

    private static void ShowIn(PreviewViewModel preview, object? loaded, string caption)
    {
        switch (loaded)
        {
            case PreviewImage picture: preview.Show(picture, caption, alphaIsCoverage: false); break;
            case UnityMesh mesh: preview.Show(mesh, caption, null); break;
            default: preview.Clear("Nothing to show for this one."); break;
        }
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
