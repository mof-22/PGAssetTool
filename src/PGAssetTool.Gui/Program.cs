using Avalonia;

namespace PGAssetTool.Gui;

internal static class Program
{
    // Avalonia has to be initialised before anything touches its types, so this stays free of
    // everything but the builder.
    [STAThread]
    public static int Main(string[] args)
    {
        // A window cannot be looked at from a build, so --self-test brings the app up, waits for
        // the game to load, reports what the model holds and exits. It is the only way to know the
        // startup path works without a person sitting in front of it.
        if (args.Contains("--self-test")) return SelfTest.Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}
