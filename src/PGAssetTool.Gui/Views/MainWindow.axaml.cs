using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using PGAssetTool.Gui.ViewModels;

namespace PGAssetTool.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Clicking a row and opening it are one gesture. Avalonia expands only on the chevron,
        // which is a second, smaller target for something this tool asks for constantly. Handled
        // here rather than on selection, because re-clicking an already selected row must still
        // fold it away.
        AddHandler(InputElement.TappedEvent, OnTapped, RoutingStrategies.Bubble);
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control) return;

        var row = control.FindAncestorOfType<TreeViewItem>();
        if (row?.DataContext is TreeNode { HasChildren: true } node) node.IsExpanded = !node.IsExpanded;
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnFocusSearch(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model) model.Workspace = 0;
        this.FindControl<TextBox>("Search")?.Focus();
    }

    private void OnReload(object? sender, RoutedEventArgs e) => _ = Model?.ReloadAsync();

    private void OnShowBrowse(object? sender, RoutedEventArgs e) => Show(0);
    private void OnShowEditor(object? sender, RoutedEventArgs e) => Show(1);
    private void OnShowManager(object? sender, RoutedEventArgs e) => Show(2);

    private void Show(int workspace)
    {
        if (Model is { } model) model.Workspace = workspace;
    }
}
