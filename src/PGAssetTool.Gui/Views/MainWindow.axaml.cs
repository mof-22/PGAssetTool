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

        // Everything else the menu does is a command on the model. Focus is the exception: the
        // control belongs to the view, so the model only reports that someone asked for it.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model) model.SearchRequested += FocusSearch;
        };
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control) return;

        var row = control.FindAncestorOfType<TreeViewItem>();
        if (row?.DataContext is TreeNode { HasChildren: true } node) node.IsExpanded = !node.IsExpanded;
    }

    private void FocusSearch() => this.FindControl<TextBox>("Search")?.Focus();

    private void OnOptions(object? sender, RoutedEventArgs e)
        => new OptionsWindow { DataContext = DataContext }.ShowDialog(this);

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
