using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PGAssetTool.Gui.ViewModels;
using PGAssetTool.Gui.Views;

namespace PGAssetTool.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };
            desktop.Exit += (_, _) => model.Dispose();

            // Opening the game reads several catalog bundles; doing it here rather than in the
            // constructor keeps the window up and captioned while it happens.
            _ = model.LoadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
