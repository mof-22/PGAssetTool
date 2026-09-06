using AssetsTools.NET.Extra;
using Avalonia;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Mods;
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
            model.Search = "";
            if (!Select(model, 16)) return Fail($"#16 never resolved: {model.Status}");

            var detail = model.Detail!;
            Console.WriteLine($"selected {detail.Name} — {detail.Subtitle}");
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
                    // Drawn the way the control would, so a rasterizer that throws or leaves an
                    // empty image is caught here rather than by a person looking at a blank pane.
                    var target = new RenderTarget();
                    target.Resize(320, 320);
                    MeshRenderer.Render(mesh, new Camera(), target, model.Preview.MeshTextures);

                    var drawn = 0;
                    var coloured = 0;
                    for (var i = 0; i < target.Bgra.Length; i += 4)
                    {
                        if (target.Bgra[i + 3] == 0) continue;
                        drawn++;
                        // Untextured shading writes equal channels; a texture almost never does.
                        if (target.Bgra[i] != target.Bgra[i + 1] || target.Bgra[i + 1] != target.Bgra[i + 2])
                            coloured++;
                    }

                    var slots = model.Preview.MeshTextures;
                    Console.WriteLine($"         rasterised {drawn:N0} of {320 * 320:N0} pixels, "
                        + $"{coloured:N0} of them coloured; textures "
                        + $"{(slots is null ? "none" : string.Join(", ", slots.Select(s => s is null ? "-" : $"{s.Width}x{s.Height}")))}");

                    if (drawn == 0) return Fail($"'{node.Label}' rendered to an empty image");
                    if (slots?.Any(s => s is not null) == true && coloured == 0)
                        return Fail($"'{node.Label}' has a texture but drew in flat grey");
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

                // Automatic until somebody disagrees, and then their answer for everything after.
                // Re-deciding per asset meant an icon came back with its alpha honoured however
                // many times in a row it had just been turned off.
                model.ToggleAlphaCommand.Execute(null);
                if (model.Preview.ShowAlpha) return Fail("the alpha toggle did not turn off");

                detail.SelectedNode = detail.Roots[0];
                detail.SelectedNode = icon;
                for (var waited = 0; model.Preview.Nothing is not null && waited < 30_000; waited += 50)
                    Thread.Sleep(50);

                Console.WriteLine($"         after turning it off, the next picture kept it off: "
                    + $"{!model.Preview.ShowAlpha}");
                if (model.Preview.ShowAlpha)
                    return Fail("the alpha choice was thrown away when the next picture loaded");

                model.ToggleAlphaCommand.Execute(null);
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
            if (model.Workspace != MainViewModel.ManagerTab) return Fail("the workspace command did nothing");
            model.ShowBrowseCommand.Execute(null);

            Console.WriteLine($"options  {model.Languages.Count} languages, settings at {model.SettingsPath}");
            if (model.Languages.Count < 2) return Fail("the game offers more than one language");

            // Switching reads the game again, so this is also the reload path.
            // A weapon is known by one name, and not necessarily the one on screen: searching the
            // Japanese name has to find it while the list is in English.
            model.Search = "究極点";
            Console.WriteLine($"search   '究極点' while displaying English -> "
                + $"{string.Join(", ", model.Weapons.Select(w => $"#{w.Record.GameNumber} {w.Name}"))}");
            if (model.Weapons.All(w => w.Record.GameNumber != 416))
                return Fail("searching a Japanese name did not find the weapon");
            model.Search = "";

            // Switching the language re-reads one bundle, not the whole game.
            var before = model.Weapons.FirstOrDefault(w => w.Record.GameNumber == 16)?.Name;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            model.Language = "l_ja";
            for (var waited = 0; model.Busy && waited < 120_000; waited += 50) Thread.Sleep(50);
            Console.WriteLine($"options  switching language took {clock.ElapsedMilliseconds}ms");
            if (clock.ElapsedMilliseconds > 3000)
                return Fail($"switching language took {clock.ElapsedMilliseconds}ms, which is a reload");

            var after = model.Weapons.FirstOrDefault(w => w.Record.GameNumber == 16)?.Name;
            Console.WriteLine($"options  #16 reads '{before}' in English, '{after}' in Japanese");
            if (before == after) return Fail("switching language changed no names");

            model.Language = "l_en-gb";
            for (var waited = 0; model.Busy && waited < 120_000; waited += 50) Thread.Sleep(50);

            // Ctrl+R throws the reader away and starts over, which is the slowest thing the GUI
            // does on purpose. It used to take fifteen seconds, nearly all of it rebuilding the
            // CAB index through a class database loaded once per bundle.
            clock.Restart();
            // Blocking rather than awaiting: an await here would resume on a pool thread, and the
            // headless window can only be closed from the one that made it.
            model.ReloadAsync().GetAwaiter().GetResult();
            for (var waited = 0; model.Busy && waited < 120_000; waited += 50) Thread.Sleep(50);
            Console.WriteLine($"reload   {clock.ElapsedMilliseconds}ms for the whole game");
            if (clock.ElapsedMilliseconds > 8000)
                return Fail($"reloading took {clock.ElapsedMilliseconds}ms");

            // With a window open the list box owns the selection, and rebuilding the collection —
            // which searching and reloading both do — clears it. So pick one again before asking
            // for it to be written out.
            if (!Select(model, 16)) return Fail("selecting #16 resolved nothing");

            // A skin is worth a row only if what it changes can be reached from it.
            if (!Select(model, 416)) return Fail("selecting #416 resolved nothing");

            var skins = model.Detail?.Roots.FirstOrDefault(r => r.Label == "Skins");
            if (skins is null) return Fail("#416 has skins and the tree showed none");

            Console.WriteLine($"skins    {skins.Children.Count} on #416, "
                + $"{skins.Children.Sum(s => CountRows(s.Children))} rows beneath them");
            foreach (var skin in skins.Children.Take(3))
                Console.WriteLine($"         {skin.Label} -> "
                    + string.Join(", ", skin.Children.Select(c => c.Label)));

            if (skins.Children.All(s => s.Children.Count == 0))
                return Fail("no skin reached a texture");

            if (!Select(model, 16)) return Fail("selecting #16 resolved nothing");

            // Closed before the writing starts: an async command raises CanExecuteChanged when it
            // finishes, and with no synchronization context here that lands on a pool thread while
            // a menu item is listening. The real app resumes on the UI thread and does not care.
            window.Close();

            // Extraction has to land somewhere sensible whatever directory the exe was launched
            // from, which is why it does not use the current one the way the CLI does.
            Console.WriteLine($"extract  writing to {model.WorkspaceRoot}");
            if (!Path.IsPathRooted(model.WorkspaceRoot))
                return Fail("the workspace root has to be absolute; a window has no current directory");

            model.ExtractWeaponCommand.Execute(null);
            for (var waited = 0; model.Busy && waited < 120_000; waited += 50) Thread.Sleep(50);
            Console.WriteLine($"extract  {model.Status}");

            if (model.LastExport is not { } written || !Directory.Exists(written))
                return Fail($"nothing was written: {model.Status}");

            var manifest = Path.Combine(written, PGAssetTool.Core.Pack.PackManifest.FileName);
            if (!File.Exists(manifest)) return Fail($"no manifest in {written}");

            var operations = PGAssetTool.Core.Pack.Workspace.Read(written).Operations;
            Console.WriteLine($"extract  {operations.Count} replaceable files across "
                + $"{operations.Select(o => o.Target.Container).Distinct().Count()} bundles");
            if (operations.Count == 0) return Fail("the manifest named nothing replaceable");


            // The editor reads the workspace that was just written, and has to notice an edit made
            // to it from outside — which is the whole point of the round trip.
            model.Editor.Rescan(model.WorkspaceRoot);
            Console.WriteLine($"editor   {model.Editor.Workspaces.Count} workspaces, "
                + $"{model.Editor.Files.Count} files in '{model.Editor.SelectedWorkspace?.Name}'");

            // The one just written, found by name: rescanning keeps whatever was selected before,
            // and the root also holds earlier extracts whose edits are not this test's business.
            var fresh = model.Editor.Workspaces.FirstOrDefault(w => w.Directory == model.LastExport);
            if (fresh is null) return Fail("the freshly extracted workspace is not listed");
            if (fresh.Edited != 0) return Fail($"'{fresh.Name}' was just extracted and shows {fresh.Edited} edited");

            model.Editor.SelectedWorkspace = fresh;
            if (model.Editor.Files.Count == 0) return Fail("the editor saw no files in a fresh extract");

            var texture = model.Editor.Files.FirstOrDefault(f => f.Name.EndsWith(".png"));
            if (texture is null) return Fail("no texture in the extracted workspace");

            model.Editor.SelectedFile = texture;
            for (var waited = 0; model.Editor.Edited.Nothing is not null && waited < 60_000; waited += 50)
                Thread.Sleep(50);
            Console.WriteLine($"editor   original: {model.Editor.Original.Caption}");
            Console.WriteLine($"editor   edited:   {model.Editor.Edited.Caption}");

            if (model.Editor.Original.Nothing is { } gameSide) return Fail($"the game side did not load: {gameSide}");
            if (model.Editor.Edited.Nothing is { } fileSide) return Fail($"the file side did not load: {fileSide}");

            // Paint over it the way an image editor would, and ask again.
            var bytes = File.ReadAllBytes(texture.FullPath);
            File.WriteAllBytes(texture.FullPath, [.. bytes, .. new byte[16]]);
            model.Editor.Refresh();

            var marked = model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath).Edited;
            Console.WriteLine($"editor   after an outside edit, marked as edited: {marked}");
            if (!marked) return Fail("an edit made outside the tool was not noticed");

            // Alpha in these textures is usually emission rather than coverage, so an author can
            // ask for the colours alone. What comes back without an alpha channel has to be given
            // the original one, or turning that option on would flatten every mask in the game.
            if (RoundTripWithoutAlpha(model) is { } alphaProblem) return Fail(alphaProblem);

            // What was installed before this run touched anything. A test that writes to the game
            // has to leave it as it found it, and the only way to know that is to have looked
            // first — an earlier version of this left a mod behind every time the workspace it
            // happened to pick changed, and nothing noticed for days.
            var wasInstalled = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read()
                .Select(m => m.Id).ToHashSet();

            // With something genuinely edited, the whole loop: build a pack and put it in the game.
            // Only files that differ are packed, so this is also what says the edit was noticed.
            model.Editor.PackAndApplyCommand.Execute(null);
            for (var waited = 0; model.Busy && waited < 300_000; waited += 50) Thread.Sleep(50);
            Console.WriteLine($"pack     {model.Status}");

            if (model.Status.Contains("failed") && !model.Status.Contains("0 failed"))
                return Fail($"applying reported failures: {model.Status}");
            if (!model.Status.Contains("applied")) return Fail($"the pack was not applied: {model.Status}");

            // Only what this run installed: someone else's mods are not this test's to touch.
            var mine = PackId(model);
            var installed = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read();
            var strays = installed.Select(m => m.Id).Where(id => id != mine && !wasInstalled.Contains(id)).ToList();
            if (strays.Count > 0)
                return Fail($"this run installed {string.Join(", ", strays)} and has no plan to remove them");
            if (installed.All(m => m.Id != mine)) return Fail($"'{mine}' is not in the ledger");
            Console.WriteLine($"pack     installed: {string.Join(", ", installed.Select(m => m.Id))}");
            if (installed.Count == 0) return Fail("nothing ended up in the ledger");

            // The manager reads the game rather than the ledger alone, so a bundle changed outside
            // this tool is visible before anyone installs over it.
            model.Manager.Refresh();
            Console.WriteLine($"manager  {model.Manager.Status}");
            Console.WriteLine($"manager  bundles differing: "
                + string.Join(", ", model.Manager.Bundles.Select(b => $"{b.Bundle} ({b.Explanation})")));

            if (model.Manager.Mods.Count == 0) return Fail("the manager saw nothing installed");
            if (model.Manager.Bundles.All(b => b.State != PGAssetTool.Core.Mods.BundleState.ChangedByThisTool))
                return Fail("the bundle just written was not attributed to the mod that wrote it");
            if (model.Manager.GameIsRunning) return Fail("the game should not be running during a self-test");

            // Turning one off restores its bundles; the confirmation is what stands between a click
            // and the game being rewritten.
            model.Manager.Selected = model.Manager.Mods.FirstOrDefault(m => m.Mod.Id == mine);
            if (model.Manager.Selected is null) return Fail($"the manager does not list '{mine}'");
            model.Manager.DisableCommand.Execute(null);
            if (model.Manager.Asking is null) return Fail("turning a mod off asked for no confirmation");

            // The buttons in that dialog have to reach the commands. Bound through the wrong
            // ancestor they resolve to nothing, Avalonia disables them, and it looks like the tool
            // refusing rather than a binding being wrong — which is exactly what happened.
            var dialog = new Views.MainWindow { DataContext = model };
            dialog.Show();
            model.Workspace = MainViewModel.ManagerTab;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var buttons = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                .Where(b => b.Content is "Cancel" or "Go ahead")
                .ToList();

            Console.WriteLine($"manager  dialog buttons: {buttons.Count}, "
                + $"enabled {buttons.Count(b => b.IsEffectivelyEnabled)}");

            var usable = buttons.Count == 2 && buttons.All(b => b.IsEffectivelyEnabled);
            dialog.Close();
            if (!usable) return Fail("the confirmation buttons are not usable");
            Console.WriteLine($"manager  asked: {model.Manager.Asking.Title}");

            model.Manager.ProceedCommand.Execute(null);
            // The manager reports its own busy state; the shell is not involved in this one.
            for (var waited = 0; (model.Manager.Busy || model.Manager.Asking is not null) && waited < 180_000;
                 waited += 50)
                Thread.Sleep(50);
            Console.WriteLine($"manager  {model.Manager.Status}");

            var mineNow = model.Manager.Mods.First(m => m.Mod.Id == mine);
            if (mineNow.Enabled) return Fail("the mod is still on after being turned off");

            // Only this mod's own bundles. Anything else installed and still on is supposed to be
            // left written — asserting the whole game went vanilla passed only for as long as this
            // test happened to be the only thing installed.
            var mineBundles = mineNow.Mod.TouchedBundles.Keys.ToHashSet();
            if (model.Manager.Bundles.Any(b =>
                    mineBundles.Contains(b.Bundle)
                    && b.State == PGAssetTool.Core.Mods.BundleState.ChangedByThisTool))
                return Fail("turning it off left one of its own bundles changed");

            // The pack is kept beside the tool, so deleting the workspace cannot strand it.
            var kept = mineNow.Mod.PackPath;
            Console.WriteLine($"manager  pack kept at {kept}");
            if (!kept.Contains("PGAssetTool-data")) return Fail("the pack was not copied into the store");

            // Removed through the manager rather than around it, so the path a person actually
            // takes is the one under test. This also puts the game back: it is a test, not a
            // change anyone asked for.
            model.Manager.RemoveCommand.Execute(null);
            if (model.Manager.Asking is null) return Fail("removing asked for no confirmation");

            model.Manager.ProceedCommand.Execute(null);
            for (var waited = 0; (model.Manager.Busy || model.Manager.Asking is not null) && waited < 180_000;
                 waited += 50)
                Thread.Sleep(50);

            Console.WriteLine($"manager  after removing: {model.Manager.Status}");
            if (model.Manager.Mods.Any(m => m.Mod.Id == mine)) return Fail("the mod is still installed");

            var left = model.Manager.Mods.Select(m => m.Mod.Id).Where(id => !wasInstalled.Contains(id)).ToList();
            Console.WriteLine($"manager  installed before this run: {wasInstalled.Count}, left behind by it: {left.Count}");
            if (left.Count > 0) return Fail($"this run left {string.Join(", ", left)} in the game");
            if (model.Status.Contains("Value cannot be null"))
                return Fail($"the shell reported an error during removal: {model.Status}");

            File.WriteAllBytes(texture.FullPath, bytes);
            model.Editor.Refresh();
            if (model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath).Edited)
                return Fail("putting the file back should clear the mark");

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

    /// Selecting resolves off the UI thread, and Detail holds the previous weapon while it does —
    /// so waiting for it to be non-null passes immediately on the wrong tree.
    /// Writes one texture with no alpha channel, reads it back through the importer and checks the
    /// original alpha survived. Answers null when it did.
    private static string? RoundTripWithoutAlpha(MainViewModel model)
    {
        if (model.Game is null) return "the game is not open";
        using var bundles = new BundleSet(model.Game);

        var file = bundles.Open("d_w");
        var info = ReferenceWalker.FindByName(bundles.Context, file, AssetClassID.Texture2D,
            "eco_rifle_map", StringComparison.OrdinalIgnoreCase);
        if (info is null) return "eco_rifle_map is not in d_w any more";

        var field = bundles.Context.Deserialize(file, info)!;
        var before = AssetPreview.Texture(bundles, "d_w", field);
        if (before is null) return "the original could not be decoded";

        long alphaBefore = 0;
        for (var i = 3; i < before.Bgra.Length; i += 4) alphaBefore += before.Bgra[i];

        var directory = Directory.CreateTempSubdirectory("pgassettool-alpha").FullName;
        try
        {
            var written = new AssetExporter(bundles) { Opaque = true }
                .Export("d_w", file, info, directory, fileNameOverride: "rgb");

            using (var stream = File.OpenRead(written[0].Path))
            {
                var read = StbImageSharp.ImageResult.FromStream(
                    stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                if (read?.SourceComp != StbImageSharp.ColorComponents.RedGreenBlue)
                    return $"the opaque export still carries {read?.SourceComp}";
            }

            var stream2 = field["m_StreamData"];
            var payload = bundles.ReadResource("d_w", stream2["path"].AsString,
                stream2["offset"].AsLong, stream2["size"].AsLong);
            var pixels = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(field)
                .DecodeTextureRaw(payload, useBgra: true);

            var again = bundles.Context.Deserialize(file, info)!;
            var change = TextureImporter.Replace(again, written[0].Path, pixels);
            if (!change.AlphaKept) return "the importer did not reuse the original alpha";

            var after = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(again);
            var decoded = after.DecodeTextureRaw(after.pictureData, useBgra: true);
            long alphaAfter = 0;
            for (var i = 3; i < decoded.Length; i += 4) alphaAfter += decoded[i];

            Console.WriteLine($"alpha    exported without it, reimported with the original back: "
                + $"mean {alphaBefore / (before.Bgra.Length / 4.0):F1} -> {alphaAfter / (decoded.Length / 4.0):F1}");

            return alphaBefore == alphaAfter ? null : "the alpha came back different";
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// The id of the pack this run built, taken from the manifest rather than guessed.
    private static string PackId(MainViewModel model)
    {
        var pack = Directory.GetFiles(model.Editor.SelectedWorkspace!.Directory, "*.pgmod").Single();
        return PGAssetTool.Core.Pack.PackBuilder.ReadManifest(pack).Id;
    }

    private static bool Select(MainViewModel model, int number)
    {
        model.Selected = model.Weapons.First(w => w.Record.GameNumber == number);
        for (var waited = 0; waited < 60_000; waited += 50)
        {
            if (model.Detail?.Tree.Record.GameNumber == number) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static int CountRows(IEnumerable<TreeNode> nodes)
        => nodes.Sum(n => 1 + CountRows(n.Children));

    private static int Fail(string why)
    {
        Console.Error.WriteLine($"SELF TEST FAILED: {why}");
        return 1;
    }
}
