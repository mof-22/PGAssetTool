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
    public static int Run()
    {
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

                if (want == AssetClassID.Mesh && model.Preview.Mesh is { } mesh)
                {
                    // Draw a frame the way the control would, so a rasterizer that throws or leaves
                    // an empty image is caught here rather than by a person looking at a blank pane.
                    var pixels = new byte[320 * 320 * 4];
                    MeshRenderer.Render(mesh, new Camera(), pixels, 320, 320);
                    var drawn = 0;
                    for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) drawn++;
                    Console.WriteLine($"         rasterised {drawn:N0} of {320 * 320:N0} pixels");
                    if (drawn == 0) return Fail($"'{node.Label}' rendered to an empty image");
                }
            }

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

    private static int Fail(string why)
    {
        Console.Error.WriteLine($"SELF TEST FAILED: {why}");
        return 1;
    }
}
