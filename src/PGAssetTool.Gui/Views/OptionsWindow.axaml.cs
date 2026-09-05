using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PGAssetTool.Gui.Views;

/// Preferences, applied as they are changed rather than on an OK button. There is nothing here
/// worth a cancel: every setting takes effect immediately and can be set back the same way.
public partial class OptionsWindow : Window
{
    public OptionsWindow() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
