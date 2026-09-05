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

            return detail.Roots.Count > 0 ? 0 : Fail("the tree came out empty");
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
