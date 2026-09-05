using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Settings;
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

    [ObservableProperty] private string _status = "Looking for the game…";
    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private WeaponListItem? _selected;
    [ObservableProperty] private WeaponDetailViewModel? _detail;

    public PreviewViewModel Preview { get; } = new();

    /// The resolved weapon behind the current tree, kept so the tree can be rebuilt when the filter
    /// changes without reading the bundles again.
    private WeaponTree? _tree;

    /// Set while preferences are being applied, so reading them back does not write them out again
    /// or reload the game once per setting.
    private bool _loading;

    /// Hides everything that cannot be written back. What counts comes from the import registry.
    [ObservableProperty] private bool _replaceableOnly = true;

    /// The game translation table names are read from, and the languages this installation offers.
    [ObservableProperty] private string _language = "l_en-gb";

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    /// Shown in the options window, because where a portable tool keeps its state is worth being
    /// able to see rather than having to guess.
    public string SettingsPath => ToolSettings.PathIn(ModStore.DefaultHome());

    /// Which workspace tab is showing: browse, editor, manager.
    [ObservableProperty] private int _workspace;

    public ObservableCollection<WeaponListItem> Weapons { get; } = [];

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
        var settings = ToolSettings.Load();
        _loading = true;
        Language = settings.Language;
        ReplaceableOnly = settings.ReplaceableOnly;
        _loading = false;

        try
        {
            await Task.Run(() =>
            {
                var game = GameInstallation.OpenDetected();
                _bundles = new BundleSet(game);
                _catalogs = GameCatalogs.Load(_bundles, Language);
                _resolver = new WeaponResolver(_bundles, _catalogs);
            });

            Languages.Clear();
            foreach (var (bundle, name) in GameCatalogs.Languages(_bundles!))
                Languages.Add(new LanguageOption(bundle, name));

            Show(_catalogs!.Items.Weapons);
            Status = $"{_catalogs.Items.Count} weapons";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
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
            : _catalogs.Items.Search(value, _catalogs.Localization));
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
            Status = ex.Message;
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

    [RelayCommand]
    private void FocusSearch()
    {
        Workspace = Browse;
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
    private void ShowBrowse() => Workspace = Browse;

    [RelayCommand]
    private void ShowEditor() => Workspace = Editor;

    [RelayCommand]
    private void ShowManager() => Workspace = Manager;

    public const int Browse = 0;
    public const int Editor = 1;
    public const int Manager = 2;

    partial void OnReplaceableOnlyChanged(bool value)
    {
        Remember();
        if (_tree is null) return;

        Preview.Clear();
        Detail = new WeaponDetailViewModel(_tree, value) { NodeSelected = ShowPreview };
    }

    /// Changing the language means every name in the catalogs, so the game is read again.
    async partial void OnLanguageChanged(string value)
    {
        Remember();
        if (!_loading && _bundles is not null) await ReloadAsync();
    }

    private void Remember()
    {
        if (_loading) return;
        try { new ToolSettings { Language = Language, ReplaceableOnly = ReplaceableOnly }.Save(); }
        catch (IOException) { }
    }

    private async void ShowPreview(TreeNode? node)
    {
        if (node?.Class is not (AssetClassID.Texture2D or AssetClassID.Mesh))
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

                    return node.Class == AssetClassID.Texture2D
                        ? AssetPreview.Texture(_bundles, node.Bundle, field)
                        : AssetPreview.Mesh(field);
                });

                switch (loaded)
                {
                    case PreviewImage picture:
                        Preview.Show(picture, $"{node.Label}   @ {node.Bundle}", node.AlphaIsCoverage);
                        break;
                    case UnityMesh mesh:
                        Preview.Show(mesh, $"{node.Label}   @ {node.Bundle}");
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
