using System.Collections.ObjectModel;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Gui.ViewModels;

/// One row in the tree. Only the leaves stand for something that can be looked at or replaced.
public sealed partial class TreeNode(
    string label, string? detail = null, AssetClassID? cls = null, long pathId = 0, string bundle = "",
    bool alphaIsCoverage = false) : ObservableObject
{
    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public AssetClassID? Class { get; } = cls;
    public long PathId { get; } = pathId;

    /// Which bundle the object lives in. A weapon spans several, so the prefab's bundle is not
    /// enough to find one of its textures again.
    public string Bundle { get; } = bundle;

    /// Whether this object's alpha channel means transparency. Icons sit on an empty background
    /// and need it; a model texture usually keeps something else there and is holed by it.
    public bool AlphaIsCoverage { get; } = alphaIsCoverage;

    public ObservableCollection<TreeNode> Children { get; } = [];

    /// Two-way bound to the row, so clicking one and expanding it are the same gesture.
    [ObservableProperty] private bool _isExpanded;

    /// A model this row stands for whose contents have not been read yet.
    ///
    /// A skin that brings its own model is recorded as a place rather than as what is in it:
    /// reading the closure of every skin would be a bundle walk per skin every time a weapon was
    /// selected, and most of them are never opened. So it is read when somebody opens the row, and
    /// the row can be opened whether or not it has anything under it yet.
    public SkinModel? Unread { get; init; }

    /// Whether the model behind this row has been asked for.
    ///
    /// Kept apart from whether the row has anything under it, which is what used to stand in for
    /// it. A skin can bring a model *and* paint the weapon, and such a row arrives with the paint
    /// already beneath it — so "it has children, it must have been read" was wrong for exactly the
    /// skins that have the most to show, and their models were never read at all.
    public bool ModelRead { get; set; }

    public bool HasChildren => Children.Count > 0 || Unread is not null;

    public string Icon => Class switch
    {
        AssetClassID.Texture2D => "🖼",
        AssetClassID.Mesh => "🧊",
        AssetClassID.AudioClip => "🔊",
        AssetClassID.Material => "🎨",
        AssetClassID.GameObject => "📦",
        // What it holds, not what has been read out of it. A skin that brings its own model has
        // nothing beneath it until somebody opens the row, and drawing those two as an empty row
        // said the skins with the most in them were the ones with nothing.
        _ => HasChildren ? "📁" : "·",
    };

    public TreeNode With(TreeNode child)
    {
        Children.Add(child);
        return this;
    }
}

public sealed partial class WeaponDetailViewModel : ObservableObject
{
    public WeaponDetailViewModel(WeaponTree tree, bool replaceableOnly)
    {
        Tree = tree;
        Roots = Build(tree, replaceableOnly);
        Watch(Roots);
    }

    public WeaponTree Tree { get; }
    public ObservableCollection<TreeNode> Roots { get; }

    /// Set by the shell so a click in the tree can be turned into a preview.
    public Action<TreeNode?>? NodeSelected { get; set; }

    /// Set by the shell so opening a row whose contents have not been read can read them. The shell
    /// owns the bundles and the lock around them; this only says which row was opened.
    public Action<TreeNode>? NodeOpened { get; set; }

    /// Watches the rows that stand for something unread, so opening one asks for it — once.
    private void Watch(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Unread is not null)
            {
                var row = node;
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(TreeNode.IsExpanded)) return;
                    if (row is not { IsExpanded: true, ModelRead: false }) return;

                    // Marked before the reading starts rather than after: it is asynchronous, and
                    // a row can be closed and opened again while the first read is still running.
                    row.ModelRead = true;
                    NodeOpened?.Invoke(row);
                };
            }

            Watch(node.Children);
        }
    }

    [ObservableProperty] private TreeNode? _selectedNode;

    partial void OnSelectedNodeChanged(TreeNode? value) => NodeSelected?.Invoke(value);

    public string Name => Tree.DisplayName;
    public string Subtitle =>
        $"#{Tree.Record.GameNumber}   {Tree.Record.PrefabName}   {Tree.Record.Slug}"
        + (Tree.PrefabBundle is null ? "" : $"   @ {Tree.PrefabBundle}");

    private static ObservableCollection<TreeNode> Build(WeaponTree tree, bool replaceableOnly)
    {
        var roots = new ObservableCollection<TreeNode>();

        // Grouped by class rather than listed flat: a weapon prefab reaches a hundred-odd objects
        // and most of them are transforms nobody wants to scroll past. Which classes are worth
        // keeping when filtered comes from the import registry, so a type gained later shows up
        // here without this file being touched.
        var byClass = tree.PrefabAssets
            .Where(a => !replaceableOnly || Replaceable.Supports(a.Class))
            .GroupBy(a => a.Class)
            .OrderBy(g => Rank(g.Key))
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal);

        var prefab = new TreeNode(tree.Record.PrefabName, $"{tree.PrefabAssets.Count} objects",
            AssetClassID.GameObject) { IsExpanded = true };

        foreach (var group in byClass)
        {
            var node = new TreeNode(group.Key.ToString(), $"{group.Count()}")
            {
                IsExpanded = Replaceable.Supports(group.Key),
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
                .With(new TreeNode(icon.TextureName, icon.AssetPath, AssetClassID.Texture2D, 0,
                    icon.Container, alphaIsCoverage: true)));

        if (tree.Skins.Count > 0)
        {
            // A skin is a set of materials, and what a modder wants from it is the textures they
            // use. The materials themselves are a step on the way and stay collapsed.
            var skins = new TreeNode("Skins", $"{tree.Skins.Count}");
            foreach (var skin in tree.Skins)
            {
                // A skin that brings its own model gets a row for it, read when it is opened. Its
                // own materials do not resolve against the usual roots — the model carries what it
                // needs — so without this there was nothing of such a skin to look at at all.
                var node = new TreeNode(skin.DisplayName ?? skin.Record.Id, skin.Record.Id)
                {
                    Unread = skin.Model,
                };

                foreach (var material in skin.Materials)
                {
                    // With one material there is nothing to distinguish, so its textures hang
                    // straight off the skin rather than behind a row that says nothing.
                    var into = node;
                    if (skin.Materials.Count > 1)
                    {
                        into = new TreeNode(material.Name, material.Bundle, AssetClassID.Material);
                        node.With(into);
                    }

                    foreach (var texture in material.Textures)
                        into.With(new TreeNode(texture.Name, material.Bundle, AssetClassID.Texture2D,
                            texture.PathId, material.Locate(texture).Bundle));
                }
                skins.With(node);
            }
            roots.Add(skins);
        }

        // Related assets are paths, not objects: nothing here can be replaced through them, so the
        // filter takes them out along with everything else that is only context.
        if (tree.Related.Count > 0 && !replaceableOnly)
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
