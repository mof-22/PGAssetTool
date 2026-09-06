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

        // Space plays the clip in front of you. Not a menu shortcut: registering it as one would
        // take the space bar away from the search box and from every button that a keyboard user
        // presses with it. Tunnelled so it is seen before a focused control acts on it, and given
        // straight back whenever typing is what space means.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Something inside the window has to hold the keyboard for a key press to have anywhere to
        // travel from. Freshly opened, nothing did, and every shortcut stayed dead until a click
        // landed somewhere. The weapon list is the right thing to hand it to: the arrow keys then
        // walk the list, which is what a person reaches for first anyway.
        Opened += (_, _) => this.FindControl<ListBox>("Weapons")?.Focus();

        // Everything else the menu does is a command on the model. Focus is the exception: the
        // control belongs to the view, so the model only reports that someone asked for it.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel model) return;

            model.SearchRequested += FocusSearch;

            // A file watcher fires on its own thread; everything it leads to touches the UI.
            model.Editor.Settled += () => Avalonia.Threading.Dispatcher.UIThread.Post(model.Editor.Refresh);
        };
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control control) return;

        var row = control.FindAncestorOfType<TreeViewItem>();
        if (row?.DataContext is TreeNode { HasChildren: true } node) node.IsExpanded = !node.IsExpanded;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        // The one shortcut that cannot be a key binding on the window: opening a dialog needs the
        // window to be its owner, which is the view's business rather than the model's.
        if (e is { Key: Key.OemComma, KeyModifiers: KeyModifiers.Control })
        {
            OnOptions(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None) return;

        // Whatever a bare key means to something being typed into, it means that. Both of the ones
        // below are a letter and a space, which is exactly what a search box is for.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        switch (e.Key)
        {
            case Key.Space when model.Showing is { HasSound: true } preview:
                preview.PlayOrStop();
                e.Handled = true;
                break;

            // Every model pane on the page, because both halves of the editor's comparison show
            // one and straightening only the half that happens to be in front would leave the two
            // at different angles — which is the one thing that comparison exists to avoid.
            case Key.R:
                foreach (var view in this.GetVisualDescendants().OfType<Controls.MeshView>())
                {
                    view.Recentre();
                    e.Handled = true;
                }
                break;
        }
    }

    private void FocusSearch() => this.FindControl<TextBox>("Search")?.Focus();

    /// Opens the folder extraction wrote to, or the root if nothing has been written yet.
    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        var path = model.LastExport ?? model.WorkspaceRoot;
        System.IO.Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
        => new OptionsWindow { DataContext = DataContext }.ShowDialog(this);

    private void OnRescan(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.Editor.Rescan(model.WorkspaceRoot);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
