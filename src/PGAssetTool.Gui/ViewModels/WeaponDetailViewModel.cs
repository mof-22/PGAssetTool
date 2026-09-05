using System.Collections.ObjectModel;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Gui.ViewModels;

/// One row in the tree. Only the leaves stand for something replaceable.
public sealed class TreeNode(
    string label, string? detail = null, AssetClassID? cls = null, long pathId = 0, string bundle = "")
{
    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public AssetClassID? Class { get; } = cls;
    public long PathId { get; } = pathId;

    /// Which bundle the object lives in. A weapon spans several, so the prefab's bundle is not
    /// enough to find one of its textures again.
    public string Bundle { get; } = bundle;
    public ObservableCollection<TreeNode> Children { get; } = [];

    public bool IsExpanded { get; set; }
    public string Icon => Class switch
    {
        AssetClassID.Texture2D => "🖼",
        AssetClassID.Mesh => "🧊",
        AssetClassID.AudioClip => "🔊",
        AssetClassID.Material => "🎨",
        AssetClassID.GameObject => "📦",
        _ => Children.Count > 0 ? "📁" : "·",
    };

    public TreeNode With(TreeNode child)
    {
        Children.Add(child);
        return this;
    }
}

public sealed partial class WeaponDetailViewModel : ObservableObject
{
    public WeaponDetailViewModel(WeaponTree tree)
    {
        Tree = tree;
        Roots = Build(tree);
    }

    public WeaponTree Tree { get; }

    /// Set by the shell so a click in the tree can be turned into a preview.
    public Action<TreeNode?>? NodeSelected { get; set; }

    [ObservableProperty] private TreeNode? _selectedNode;

    partial void OnSelectedNodeChanged(TreeNode? value) => NodeSelected?.Invoke(value);
    public ObservableCollection<TreeNode> Roots { get; }

    public string Name => Tree.DisplayName;
    public string Subtitle =>
        $"#{Tree.Record.GameNumber}   {Tree.Record.PrefabName}   {Tree.Record.Slug}"
        + (Tree.PrefabBundle is null ? "" : $"   @ {Tree.PrefabBundle}");

    private static ObservableCollection<TreeNode> Build(WeaponTree tree)
    {
        var roots = new ObservableCollection<TreeNode>();

        // Grouped by class rather than listed flat: a weapon prefab reaches a few hundred objects
        // and most of them are transforms nobody wants to scroll past.
        var byClass = tree.PrefabAssets
            .GroupBy(a => a.Class)
            .OrderBy(g => Rank(g.Key))
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal);

        var prefab = new TreeNode(tree.Record.PrefabName, $"{tree.PrefabAssets.Count} objects",
            AssetClassID.GameObject) { IsExpanded = true };

        foreach (var group in byClass)
        {
            // The sound fields drive behaviour as well as playback, so the clips are listed but the
            // MonoBehaviour holding them is not something to open and edit here.
            var node = new TreeNode(group.Key.ToString(), $"{group.Count()}")
            {
                IsExpanded = Replaceable.Contains(group.Key),
            };
            foreach (var asset in group.OrderBy(a => a.Name, StringComparer.Ordinal))
                node.With(new TreeNode(
                    asset.Name.Length > 0 ? asset.Name : $"(unnamed {asset.PathId})",
                    Where(asset, tree) == tree.PrefabBundle ? null : Where(asset, tree),
                    asset.Class, asset.PathId, Where(asset, tree)));
            prefab.With(node);
        }
        roots.Add(prefab);

        if (tree.Icon is { } icon)
            roots.Add(new TreeNode("Icon", icon.Container, AssetClassID.Texture2D)
                .With(new TreeNode(icon.TextureName, icon.AssetPath, AssetClassID.Texture2D, 0, icon.Container)));

        if (tree.Skins.Count > 0)
        {
            var skins = new TreeNode("Skins", $"{tree.Skins.Count}");
            foreach (var skin in tree.Skins)
                skins.With(new TreeNode(skin.DisplayName ?? skin.Record.Id, skin.Record.Id));
            roots.Add(skins);
        }

        if (tree.Related.Count > 0)
        {
            var related = new TreeNode("Related", $"{tree.Related.Count}");
            foreach (var group in tree.Related.GroupBy(r => r.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var space = new TreeNode(group.Key, $"{group.Count()}");
                foreach (var asset in group)
                    space.With(new TreeNode(asset.Path, asset.Bundle));
                related.With(space);
            }
            roots.Add(related);
        }

        return roots;
    }

    /// An object reached across a bundle boundary belongs to whichever bundle it was found in.
    private static string Where(AssetNode asset, WeaponTree tree)
        => asset.Bundle.Length > 0 ? asset.Bundle : tree.PrefabBundle ?? "";

    private static readonly HashSet<AssetClassID> Replaceable =
        [AssetClassID.Texture2D, AssetClassID.Mesh, AssetClassID.AudioClip];

    /// What a modder came to find goes at the top; the scaffolding sinks.
    private static int Rank(AssetClassID cls) => cls switch
    {
        AssetClassID.Texture2D => 0,
        AssetClassID.Mesh => 1,
        AssetClassID.AudioClip => 2,
        AssetClassID.Material => 3,
        AssetClassID.GameObject => 4,
        _ => 5,
    };
}
