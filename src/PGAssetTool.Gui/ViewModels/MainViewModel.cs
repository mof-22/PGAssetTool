using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Catalog;
using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Game;
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
                Preview.Clear();
                Detail = new WeaponDetailViewModel(tree) { NodeSelected = ShowPreview };
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
    private async void ShowPreview(TreeNode? node)
    {
        if (node?.Class is not (AssetClassID.Texture2D or AssetClassID.Mesh) || node.PathId == 0)
        {
            Preview.Clear(node?.Class is null ? null : $"No preview for {node.Class}.");
            return;
        }

        var bundle = node.Bundle;
        if (bundle.Length == 0) { Preview.Clear("This object's bundle is not known."); return; }

        try
        {
            await _reading.WaitAsync();
            try
            {
                var loaded = await Task.Run(() =>
                {
                    var file = _bundles!.Open(bundle);
                    var info = file.file.GetAssetInfo(node.PathId);
                    if (info is null) return null;

                    var field = _bundles.Context.Deserialize(file, info);
                    if (field is null) return null;

                    return node.Class == AssetClassID.Texture2D
                        ? AssetPreview.Texture(_bundles, bundle, field)
                        : (object?)AssetPreview.Mesh(field);
                });

                switch (loaded)
                {
                    case PreviewImage picture: Preview.Show(picture, $"{node.Label}   @ {bundle}"); break;
                    case UnityMesh mesh: Preview.Show(mesh, $"{node.Label}   @ {bundle}"); break;
                    default: Preview.Clear($"'{node.Label}' could not be decoded."); break;
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
