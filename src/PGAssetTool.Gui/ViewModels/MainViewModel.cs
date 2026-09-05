using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Game;
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

    public ObservableCollection<WeaponListItem> Weapons { get; } = [];

    public string Title => _catalogs is null
        ? "PGAssetTool"
        : $"PGAssetTool — {Weapons.Count} of {_catalogs.Items.Count} weapons";

    public async Task LoadAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var game = GameInstallation.OpenDetected();
                _bundles = new BundleSet(game);
                _catalogs = GameCatalogs.Load(_bundles);
                _resolver = new WeaponResolver(_bundles, _catalogs);
            });

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
                Detail = new WeaponDetailViewModel(tree);
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
