using AssetsTools.NET.Extra;
using Avalonia;
using Avalonia.Headless;
using PGAssetTool.Core.Preview;
using PGAssetTool.Gui.ViewModels;

namespace PGAssetTool.Gui;

/// Exercises the view models against the real game without opening a window.
///
/// Everything the GUI does before a person touches it — find the installation, load the catalogs,
/// list the weapons, resolve one and build its tree — happens here too, so a break in that path
/// shows up as a failed run rather than a window that opens empty.
internal static class SelfTest
{
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);


    public static int Run()
    {
        // A WinExe starts with no console, so a published build writing to one would print into
        // nowhere. Borrowing the terminal that launched it is what makes --self-test usable on the
        // thing that actually ships, rather than only under `dotnet run`.
        // Only when nothing is already capturing the output. A pipe or a redirect means someone is,
        // and attaching replaces the handles underneath them — which swallows every line rather
        // than printing it.
        if (!Console.IsOutputRedirected) AttachConsole(AttachParentProcess);

        // The tree labels carry emoji; the Windows console defaults to a code page that mangles them.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }
        // Bitmaps need a rendering backend even when nothing is shown, so the headless one stands
        // in for a window. Everything below it is the same code the real app runs.
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();

        // Setup installs Avalonia's dispatcher as the synchronization context, and nothing here runs
        // its loop — every await would queue a continuation that never executes. Without a window
        // there is no UI thread to marshal back to, so continuations belong on the thread pool.
        SynchronizationContext.SetSynchronizationContext(null);

        return Body();
    }

    private static int Body()
    {
        var model = new MainViewModel();
        try
        {
            model.LoadAsync().GetAwaiter().GetResult();
            Console.WriteLine($"status   {model.Status}");
            Console.WriteLine($"weapons  {model.Weapons.Count}");
            if (model.Weapons.Count == 0) return Fail("the catalog produced no weapons");

            model.Search = "beretta";
            Console.WriteLine($"search   'beretta' -> {model.Weapons.Count}");

            model.Search = "";
            var wanted = model.Weapons.FirstOrDefault(w => w.Record.GameNumber == 16)
                ?? model.Weapons[0];

            model.Selected = wanted;
            for (var waited = 0; model.Detail is null && waited < 60_000; waited += 50) Thread.Sleep(50);

            if (model.Detail is not { } detail) return Fail($"'{wanted.Name}' never resolved: {model.Status}");
            Console.WriteLine($"selected {detail.Name} — {detail.Subtitle}");

            foreach (var root in detail.Roots)
            {
                Console.WriteLine($"  {root.Icon} {root.Label}  {root.Detail}");
                foreach (var child in root.Children.Take(6))
                    Console.WriteLine($"      {child.Icon} {child.Label}  {child.Detail}");
                if (root.Children.Count > 6) Console.WriteLine($"      … {root.Children.Count - 6} more");
            }

            if (detail.Roots.Count == 0) return Fail("the tree came out empty");

            // The previews are the half of the GUI a self-test can still check: decoding a texture
            // and unpacking a mesh happen in the core, and only the drawing needs a window.
            foreach (var want in new[] { AssetClassID.Texture2D, AssetClassID.Mesh })
            {
                var node = detail.Roots
                    .SelectMany(r => r.Children)
                    .SelectMany(g => g.Children)
                    .FirstOrDefault(n => n.Class == want && n.PathId != 0);

                if (node is null) { Console.WriteLine($"preview  no {want} to try"); continue; }

                // Cleared first, or the wait below would pass instantly on the previous asset.
                model.Preview.Clear();
                detail.SelectedNode = node;
                for (var waited = 0; model.Preview.Nothing is not null && waited < 30_000; waited += 50)
                    Thread.Sleep(50);

                if (model.Preview.Nothing is { } why) return Fail($"{want} '{node.Label}': {why}");
                Console.WriteLine($"preview  {model.Preview.Caption}");

                // A texture reached through a material carries something other than coverage in its
                // alpha — emission, usually — so honouring it would punch holes in the picture.
                if (want == AssetClassID.Texture2D && model.Preview.ShowAlpha)
                    return Fail($"'{node.Label}' is a model texture and was shown with alpha honoured");

                if (want == AssetClassID.Mesh && model.Preview.Mesh is { } mesh)
                {
                    // Draw a frame the way the control would, so a rasterizer that throws or leaves
                    // an empty image is caught here rather than by a person looking at a blank pane.
                    var target = new RenderTarget();
                    target.Resize(320, 320);
                    MeshRenderer.Render(mesh, new Camera(), target);
                    var drawn = 0;
                    for (var i = 3; i < target.Bgra.Length; i += 4) if (target.Bgra[i] != 0) drawn++;
                    Console.WriteLine($"         rasterised {drawn:N0} of {320 * 320:N0} pixels");
                    if (drawn == 0) return Fail($"'{node.Label}' rendered to an empty image");
                }
            }

            // The icon is the one thing addressed by name rather than by path id — the lookup table
            // records no id for it — and for the newest weapons it lives in the game's own
            // resources.assets rather than a bundle. It was silently unpreviewable.
            var icon = detail.Roots
                .FirstOrDefault(r => r.Label == "Icon")?.Children.FirstOrDefault();

            if (icon is null) Console.WriteLine("preview  this weapon has no icon");
            else
            {
                model.Preview.Clear();
                detail.SelectedNode = icon;
                for (var waited = 0; model.Preview.Nothing is not null && waited < 30_000; waited += 50)
                    Thread.Sleep(50);

                if (model.Preview.Nothing is { } why) return Fail($"icon '{icon.Label}': {why}");
                Console.WriteLine($"preview  {model.Preview.Caption}");
                Console.WriteLine($"         alpha honoured: {model.Preview.ShowAlpha} (an icon sits on nothing)");

                if (!model.Preview.ShowAlpha) return Fail("an icon was shown with its alpha ignored");
            }

            // The filter is what the tree shows by default, and it has to come from the import
            // registry rather than a list kept here, so a type gained later needs no edit.
            var everything = CountRows(detail.Roots);
            model.ReplaceableOnly = false;
            if (model.Detail is not { } unfiltered) return Fail("turning the filter off lost the tree");
            var all = CountRows(unfiltered.Roots);
            Console.WriteLine($"filter   {everything} rows replaceable-only, {all} rows unfiltered");
            if (all <= everything) return Fail("the filter hid nothing");

            model.ReplaceableOnly = true;
            if (model.Detail is not { } refiltered || CountRows(refiltered.Roots) != everything)
                return Fail("turning the filter back on did not restore the tree");

            // Everything above exercises the models. The window is where a binding can quietly
            // undo them, so it is built and read back too.
            var window = new Views.MainWindow { DataContext = model };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Console.WriteLine($"window   filter is {model.ReplaceableOnly} after the window bound to it");
            if (!model.ReplaceableOnly)
                return Fail("binding the window turned the replaceable-only filter off");

            var bound = model.Detail?.Roots.FirstOrDefault()?.Children.Select(c => c.Label).ToList() ?? [];
            Console.WriteLine($"window   classes shown: {string.Join(", ", bound)}");
            if (bound.Contains("Transform") || bound.Contains("MonoScript"))
                return Fail("the filter is not being applied to the tree the window shows");

            // InputGesture only prints the shortcut beside the menu item; HotKey is what registers
            // it. The first build shipped the former alone, so every key did nothing while the menu
            // claimed otherwise. This asks the window what it will actually respond to.
            var registered = window.KeyBindings.Select(b => b.Gesture?.ToString()).ToList();
            Console.WriteLine($"keys     {string.Join(", ", registered)}");

            foreach (var gesture in new[] { "Ctrl+F", "Ctrl+R", "Ctrl+Shift+R", "Ctrl+Shift+A", "Ctrl+D1", "Ctrl+D2", "Ctrl+D3" })
                if (!registered.Contains(gesture))
                    return Fail($"{gesture} is shown in the menu but not bound to anything");

            // And the toggle has to survive being driven, since a two-way binding on a checkable
            // menu item was what turned the filter off as soon as the menu was opened.
            model.ToggleReplaceableOnlyCommand.Execute(null);
            if (model.ReplaceableOnly) return Fail("the filter command did not turn it off");
            model.ToggleReplaceableOnlyCommand.Execute(null);
            if (!model.ReplaceableOnly) return Fail("the filter command did not turn it back on");

            model.ShowManagerCommand.Execute(null);
            if (model.Workspace != MainViewModel.Manager) return Fail("the workspace command did nothing");
            model.ShowBrowseCommand.Execute(null);

            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
        finally
        {
            model.Dispose();
        }
    }

    private static int CountRows(IEnumerable<TreeNode> nodes)
        => nodes.Sum(n => 1 + CountRows(n.Children));

    private static int Fail(string why)
    {
        Console.Error.WriteLine($"SELF TEST FAILED: {why}");
        return 1;
    }
}
