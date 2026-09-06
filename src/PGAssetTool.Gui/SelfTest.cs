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

        // Avalonia's dispatcher stays the synchronization context, as it is in the real app, and
        // WaitWhile runs it. Clearing it instead put every continuation on the thread pool, which
        // works right up until one of them touches a collection a control is bound to — and then
        // fails with "call from invalid thread" somewhere far from the cause. The whole point of
        // this test is to be the app, so it waits the way the app does.
        return Body();
    }


    /// What this test's own pack is called wherever it turns up: in the workspace list, in the
    /// manager, in the mods folder. Nothing a person would build is named this, so nothing this test
    /// removes can be theirs by accident.
    private const string PackName = "PGAssetTool self test";
    private const string PackIdentity = "pgassettool-selftest";
    private const string PackFolder = "pgassettool-selftest";

    /// A second, so batch operations have more than one thing to act on.
    private const string PackNameB = "PGAssetTool self test B";
    private const string PackIdentityB = "pgassettool-selftest-b";
    private const string PackFolderB = "pgassettool-selftest-b";

    /// Waits for something to settle while running the dispatcher, the way a live window does.
    ///
    /// Once a window has been created, Avalonia's dispatcher is the synchronization context that
    /// awaits are posted back to. A test that only sleeps never runs those, so anything that
    /// awaited while the window was up stops there for good — still holding whatever lock it took.
    /// That looked exactly like a deadlock in the tool, and was not one.
    /// Waits for one task, running the dispatcher meanwhile, and rethrows what it threw.
    ///
    /// Blocking on it instead would deadlock: its continuations are posted to this very thread.
    private static void Settle(Task task, string what)
    {
        if (!WaitWhile(() => !task.IsCompleted, 300_000))
            throw new TimeoutException($"{what} did not finish");
        task.GetAwaiter().GetResult();
    }

    private static bool WaitWhile(Func<bool> busy, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 25)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (!busy()) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static int Body()
    {
        // Its own directory, thrown away afterwards. This test extracts, edits, packs and installs
        // for real; doing that in the author's workspace put its scratch work in the same list as
        // theirs and, when the selection drifted, packed one of theirs instead.
        var scratch = Directory.CreateTempSubdirectory("pgassettool-selftest").FullName;
        var model = new MainViewModel { WorkspaceOverride = scratch, SettingsHome = scratch };
        try
        {
            Settle(model.LoadAsync(), "opening the game");
            Console.WriteLine($"status   {model.Status}");

            // Before anything, not after. This test writes to the game for real, and it used to ask
            // this question only once it reached the manager — by which point it had already
            // installed two mods into a game somebody was playing.
            if (model.Game is { } open && PGAssetTool.Core.Game.GameProcess.IsRunning(open))
                return Fail("the game is running; close it before running a test that writes to it");

            // The author's own preferences, as they stand. This test drives the filter, the language
            // and the alpha toggle, and each of those is written back the moment it moves — so runs
            // were quietly rewriting the settings file beside the executable, and one left the asset
            // tree filter switched off. Compared again at the end rather than trusted.
            var theirSettings = PGAssetTool.Core.Settings.ToolSettings.PathIn(ModStore.DefaultHome());
            var theirPreferences = File.Exists(theirSettings) ? File.ReadAllText(theirSettings) : null;
            Console.WriteLine($"status   preferences for this run: {model.SettingsPath}");

            if (ReaderReleasesItsBundles(model) is { } stillOpen) return Fail(stillOpen);
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
            foreach (var want in new[] { AssetClassID.Texture2D, AssetClassID.Mesh, AssetClassID.AudioClip })
            {
                var node = detail.Roots
                    .SelectMany(r => r.Children)
                    .SelectMany(g => g.Children)
                    .FirstOrDefault(n => n.Class == want && n.PathId != 0);

                if (node is null) { Console.WriteLine($"preview  no {want} to try"); continue; }

                // Cleared first, or the wait below would pass instantly on the previous asset.
                model.Preview.Clear();
                detail.SelectedNode = node;
                WaitWhile(() => model.Preview.Nothing is not null, 30_000);

                if (model.Preview.Nothing is { } why) return Fail($"{want} '{node.Label}': {why}");
                Console.WriteLine($"preview  {model.Preview.Caption}");

                // A texture reached through a material carries something other than coverage in its
                // alpha — emission, usually — so honouring it would punch holes in the picture.
                if (want == AssetClassID.Texture2D && model.Preview.ShowAlpha)
                    return Fail($"'{node.Label}' is a model texture and was shown with alpha honoured");

                if (want == AssetClassID.AudioClip && model.Preview.Sound is { } clip)
                {
                    // The envelope is what the pane draws; a clip that decodes to silence would
                    // otherwise look like a working preview of a flat line.
                    var envelope = clip.Envelope(320);
                    var loudest = envelope.Max(c => Math.Max(Math.Abs(c.Low), Math.Abs(c.High)));
                    Console.WriteLine($"         {clip.Frames:N0} frames, peak {loudest:0.00}, "
                        + $"{clip.ToWave().Length:N0} bytes as a wave");

                    if (clip.Frames == 0) return Fail($"'{node.Label}' decoded to no audio at all");
                    if (loudest <= 0) return Fail($"'{node.Label}' decoded to silence");

                    // What the player is handed has to be a wave Windows will take; the exporter
                    // used to produce one that declared itself empty.
                    var wave = clip.ToWave();
                    var again = PGAssetTool.Core.Import.Audio.WaveFile.Parse(wave, "the preview");
                    if (again.Frames != clip.Frames)
                        return Fail($"the wave handed to the player holds {again.Frames} of {clip.Frames} frames");
                }

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

                    // Wearing a texture chosen by hand is how a skin gets tried on a model the
                    // automatic answer knows nothing about. It silently did nothing: the choice
                    // carried its picture, filling that in made a different value, and the combo
                    // box dropped a selection it could no longer find in its own list.
                    if (model.Preview.TextureChoices.FirstOrDefault(c => c.PathId != 0) is { } wanted)
                    {
                        model.Preview.ChosenTexture = wanted;
                        WaitWhile(() => model.Preview.MeshTextures == slots, 30_000);

                        var worn = model.Preview.MeshTextures;
                        Console.WriteLine($"         wearing '{wanted.Name}': "
                            + $"{(worn is null ? "nothing" : string.Join(", ", worn.Select(s => s is null ? "-" : $"{s.Width}x{s.Height}")))}");

                        if (model.Preview.ChosenTexture != wanted)
                            return Fail("the chosen texture did not stay chosen");
                        if (worn is null || worn.Any(s => s is null))
                            return Fail($"'{wanted.Name}' was chosen and the model is still wearing the old one");
                        if (worn.Distinct().Count() != 1)
                            return Fail("a texture chosen by hand has to cover the whole model");

                        model.Preview.ChosenTexture = model.Preview.TextureChoices[0];
                        if (model.Preview.MeshTextures != slots)
                            return Fail("going back to the automatic answer did not restore it");
                    }
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
                WaitWhile(() => model.Preview.Nothing is not null, 30_000);

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
                WaitWhile(() => model.Preview.Nothing is not null, 30_000);

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

            // InputGesture only prints the shortcut beside the menu item; something has to register
            // it. The first build shipped the former alone, so every key did nothing while the menu
            // claimed otherwise. Then they were registered per menu item, which registers them when
            // that item is built — and a menu's items are not built until it has been opened once,
            // so every shortcut was still dead until its own menu had been pulled down.
            //
            // Asked of a window that has never been shown, let alone had a menu opened, and with
            // the commands checked rather than only the gestures: a binding that resolves to
            // nothing is registered and does nothing, which is the same failure wearing a hat.
            var untouched = new Views.MainWindow { DataContext = model };
            var registered = untouched.KeyBindings.ToList();
            Console.WriteLine($"keys     {string.Join(", ", registered.Select(b => b.Gesture?.ToString()))}"
                + $" (on a window nobody has opened)");

            foreach (var gesture in new[]
                     { "Ctrl+E", "Ctrl+Shift+E", "Ctrl+F", "Ctrl+R", "F5", "Ctrl+Shift+R",
                       "Ctrl+Shift+A", "Ctrl+D1", "Ctrl+D2", "Ctrl+D3" })
            {
                var binding = registered.FirstOrDefault(b => b.Gesture?.ToString() == gesture);
                if (binding is null) return Fail($"{gesture} is shown in the menu but not bound to anything");
                if (binding.Command is null) return Fail($"{gesture} is bound to nothing that can run");
            }
            untouched.Close();

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
            WaitWhile(() => model.Busy, 120_000);
            Console.WriteLine($"options  switching language took {clock.ElapsedMilliseconds}ms");
            if (clock.ElapsedMilliseconds > 3000)
                return Fail($"switching language took {clock.ElapsedMilliseconds}ms, which is a reload");

            var after = model.Weapons.FirstOrDefault(w => w.Record.GameNumber == 16)?.Name;
            Console.WriteLine($"options  #16 reads '{before}' in English, '{after}' in Japanese");
            if (before == after) return Fail("switching language changed no names");

            model.Language = "l_en-gb";
            WaitWhile(() => model.Busy, 120_000);

            // Ctrl+R throws the reader away and starts over, which is the slowest thing the GUI
            // does on purpose. It used to take fifteen seconds, nearly all of it rebuilding the
            // CAB index through a class database loaded once per bundle.
            clock.Restart();
            // Blocking rather than awaiting: an await here would resume on a pool thread, and the
            // headless window can only be closed from the one that made it.
            Settle(model.ReloadAsync(), "reloading");
            WaitWhile(() => model.Busy, 120_000);
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
            WaitWhile(() => model.Busy, 120_000);
            Console.WriteLine($"extract  {model.Status}");

            if (model.LastExport is not { } exported || !Directory.Exists(exported))
                return Fail($"nothing was written: {model.Status}");

            if (!File.Exists(Path.Combine(exported, PGAssetTool.Core.Pack.PackManifest.FileName)))
                return Fail($"no manifest in {exported}");

            // Renamed before anything is built from it, folder and manifest both. Left alone this
            // pack would be called "Hitman Pistol", built into 0016_Beretta.pgmod and filed in the
            // mods folder beside a real mod of the same weapon — twenty of them accumulated there
            // before anyone looked. Under its own name it is obvious, and it can only remove its own.
            //
            // Through the editor rather than around it: renaming the directory underneath a live
            // file watcher is refused by Windows, and the editor is what knows to put the watcher
            // down first. So this is also the test of that.
            var written = Rename(model, exported, PackFolder, PackName, PackIdentity);
            if (written is null) return Fail($"the workspace could not be renamed: {model.Editor.Status}");

            var identified = PGAssetTool.Core.Pack.Workspace.Read(written);
            var operations = identified.Operations;
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
            var fresh = model.Editor.Workspaces.FirstOrDefault(w => string.Equals(Path.GetFullPath(w.Directory), written, StringComparison.OrdinalIgnoreCase));
            if (fresh is null) return Fail("the freshly extracted workspace is not listed");
            if (fresh.Edited != 0) return Fail($"'{fresh.Name}' was just extracted and shows {fresh.Edited} edited");

            model.Editor.SelectedWorkspace = fresh;
            if (model.Editor.Files.Count == 0) return Fail("the editor saw no files in a fresh extract");

            // The pack's descriptive half is editable from the editor. Saving it must not disturb
            // the operations underneath: they carry the baseline hashes that decide what gets
            // packed, and losing those would make every untouched file look edited.
            if (model.Editor.PackName != PackName)
                return Fail($"the form shows '{model.Editor.PackName}', not what the manifest says");

            model.Editor.PackAuthor = "self test";
            model.Editor.PackVersion = "9.9.9";
            model.Editor.ApplyDetailsCommand.Execute(null);

            var saved = PGAssetTool.Core.Pack.Workspace.Read(written);
            Console.WriteLine($"editor   pack details saved: {saved.Name} {saved.Version} by {saved.Author}");
            if (saved.Author != "self test" || saved.Version != "9.9.9")
                return Fail($"the pack details did not reach the manifest: {model.Editor.Status}");
            if (PGAssetTool.Core.Pack.Workspace.Changed(written, saved).Count != 0)
                return Fail("saving the details made untouched files look edited");

            if (PackIconIsTheModel(model, written) is { } iconProblem) return Fail(iconProblem);

            var texture = model.Editor.Files.FirstOrDefault(f => f.Name.EndsWith(".png"));
            if (texture is null) return Fail("no texture in the extracted workspace");

            model.Editor.SelectedFile = texture;
            WaitWhile(() => model.Editor.Edited.Nothing is not null, 60_000);
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

            // A second workspace, so what gets tested is the batch. Building and applying six packs
            // one at a time is six trips through the same controls, and — because applying is a
            // whole-game reconcile — six rebuilds of the game with five half-finished states along
            // the way. One pack proves nothing about any of that.
            var secondary = SecondWorkspace(model, weapon: 2, PackFolderB, PackIdentityB, PackNameB);
            if (secondary is null) return Fail("the second workspace could not be prepared");

            model.Editor.Rescan(model.WorkspaceRoot);
            foreach (var w in model.Editor.Workspaces) model.Editor.Selection.Add(w);

            var chosen = model.Editor.Chosen;
            Console.WriteLine($"batch    {chosen.Count} workspaces chosen: "
                + string.Join(", ", chosen.Select(w => w.Name)));
            if (chosen.Count != 2) return Fail($"two workspaces were selected and {chosen.Count} are being acted on");

            // Exactly one file was touched in each, so exactly one belongs in each pack. Printed
            // because a pack that quietly grew to everything in its workspace is indistinguishable
            // from a working one until it is applied and writes five bundles instead of one.
            foreach (var w in chosen)
            {
                var differ = PGAssetTool.Core.Pack.Workspace.Changed(
                    w.Directory, PGAssetTool.Core.Pack.Workspace.Read(w.Directory));
                Console.WriteLine($"batch    {w.Name}: {differ.Count} of {w.Files} files differ"
                    + (differ.Count > 0 ? $" ({string.Join(", ", differ.Select(o => o.Source))})" : ""));
                if (differ.Count != 1)
                    return Fail($"one file was edited in '{w.Name}' and {differ.Count} are about to be packed");
            }

            // The whole loop, for both at once: build, install, and come back with the game read
            // again. Only files that differ are packed, so this is also what says the edits landed.
            model.Editor.PackAndApplyCommand.Execute(null);
            WaitWhile(() => model.Busy, 300_000);
            Console.WriteLine($"batch    {model.Status}");

            if (model.Status.Contains("failed") && !model.Status.Contains("0 failed"))
                return Fail($"applying reported failures: {model.Status}");
            if (!model.Status.Contains("applied")) return Fail($"the packs were not applied: {model.Status}");

            // Only what this run installed: someone else's mods are not this test's to touch.
            var mine = new[] { PackIdentity, PackIdentityB };
            var installed = new PGAssetTool.Core.Mods.ModStore(model.Game!).Read();
            var strays = installed.Select(m => m.Id)
                .Where(id => !mine.Contains(id) && !wasInstalled.Contains(id)).ToList();
            if (strays.Count > 0)
                return Fail($"this run installed {string.Join(", ", strays)} and has no plan to remove them");

            Console.WriteLine($"batch    installed: {string.Join(", ", installed.Select(m => m.Id))}");
            var missing = mine.Where(id => installed.All(m => m.Id != id)).ToList();
            if (missing.Count > 0)
                return Fail($"{string.Join(", ", missing)} did not reach the ledger — a batch that installed some");

            // The manager reads the game rather than the ledger alone, so a bundle changed outside
            // this tool is visible before anyone installs over it.
            model.Manager.Refresh();
            Console.WriteLine($"manager  {model.Manager.Status}");
            Console.WriteLine($"manager  bundles differing: "
                + string.Join(", ", model.Manager.Bundles.Select(b => $"{b.Bundle} ({b.Explanation})")));

            if (model.Manager.Mods.Count == 0) return Fail("the manager saw nothing installed");

            // The pack's picture has to survive the whole way: chosen at extraction, written into
            // the pack even though it is not one of the files being replaced, and read back out
            // where the mod is listed. Every step of that is somewhere it could quietly go missing.
            foreach (var row in model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id)))
            {
                var named = PGAssetTool.Core.Pack.PackBuilder.ReadManifest(row.Mod.PackPath).Icon;
                var picture = PGAssetTool.Core.Pack.PackBuilder.ReadIcon(row.Mod.PackPath);

                // Measured from the bytes, not from the Bitmap: without a window there is nothing to
                // decode an image into, so its size reads as one pixel however good the file is.
                var size = picture is null
                    ? null
                    : StbImageSharp.ImageInfo.FromStream(new MemoryStream(picture));

                Console.WriteLine($"manager  '{row.Name}' shows '{named}': "
                    + $"{(size is { } s ? $"{s.Width}x{s.Height}, {picture!.Length:N0} bytes" : "nothing")}");

                if (named.Length == 0) return Fail($"'{row.Name}' was built with no icon named");
                if (!row.HasIcon) return Fail($"'{row.Name}' names '{named}' and the pack has no such picture");
                if (size is not { Width: > 1, Height: > 1 })
                    return Fail($"'{row.Name}' carries '{named}' and it is not a picture");
            }
            if (model.Manager.Bundles.All(b => b.State != PGAssetTool.Core.Mods.BundleState.ChangedByThisTool))
                return Fail("the bundle just written was not attributed to the mod that wrote it");
            if (model.Manager.GameIsRunning) return Fail("the game should not be running during a self-test");

            // One first. Turning it off restores its bundles; the confirmation is what stands
            // between a click and the game being rewritten.
            model.Manager.Selected = model.Manager.Mods.FirstOrDefault(m => m.Mod.Id == PackIdentity);
            if (model.Manager.Selected is null) return Fail($"the manager does not list '{PackIdentity}'");
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

            // What is installed is shown as tiles with the picture on top, so a picture that never
            // reaches a control is the whole point of the tab going missing. Counted from the
            // window rather than the model: the model held its icons correctly the entire time the
            // combo box was dropping the texture chosen for a mesh.
            var showing = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .Count(i => i.Source is not null);
            var expected = model.Manager.Mods.Count(m => m.HasIcon);
            Console.WriteLine($"manager  {expected} of {model.Manager.Mods.Count} mods carry a picture, "
                + $"{showing} reached a tile");
            if (showing < expected)
                return Fail($"{expected} mods have a picture and only {showing} of them are being shown");

            // Ctrl and the wheel resize the tiles, and the tiles have to follow. Measured off the
            // controls: the size is a number on the model whatever happens, and a binding that
            // resolves to nothing leaves the tiles their default size while it changes.
            var was = model.Manager.TileSize;
            var tileWas = Tile(dialog);
            for (var i = 0; i < 4; i++) model.Manager.ResizeTiles(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var tileNow = Tile(dialog);
            Console.WriteLine($"manager  tiles {was} -> {model.Manager.TileSize} px, "
                + $"drawn {tileWas?.ToString() ?? "?"} -> {tileNow?.ToString() ?? "?"}");

            if (model.Manager.TileSize <= was) return Fail("the tiles did not grow");
            if (tileWas is null || tileNow is null) return Fail("no tile to measure");
            if (tileNow <= tileWas) return Fail("the tiles were resized and the drawing did not follow");

            for (var i = 0; i < 40; i++) model.Manager.ResizeTiles(-1);
            if (model.Manager.TileSize != ManagerViewModel.SmallestTile)
                return Fail($"shrinking without end reached {model.Manager.TileSize}");
            for (var i = 0; i < 80; i++) model.Manager.ResizeTiles(1);
            if (model.Manager.TileSize != ManagerViewModel.LargestTile)
                return Fail($"growing without end reached {model.Manager.TileSize}");
            model.Manager.TileSize = was;

            // And the pack's own words, read out of the pack rather than out of the ledger.
            if (model.Manager.Details is not { } about) return Fail("the selected mod shows no details");
            Console.WriteLine($"manager  '{about.Name}' {about.Summary}, {about.Changes.ToLowerInvariant()}, "
                + $"{about.Built}");
            if (about.Id != PackIdentity) return Fail($"the details are for '{about.Id}', not the selected mod");
            if (about.Replaces.Count == 0) return Fail("the pack replaces something and the details say nothing");

            // The lists themselves have to accept more than one row, or none of the above is
            // reachable by anyone actually using the window.
            var lists = dialog.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>()
                .Select(l => l.SelectionMode).ToList();
            Console.WriteLine($"manager  dialog buttons: {buttons.Count}, "
                + $"enabled {buttons.Count(b => b.IsEffectivelyEnabled)}; "
                + $"lists accepting several rows: {lists.Count(m => m.HasFlag(Avalonia.Controls.SelectionMode.Multiple))}");

            var usable = buttons.Count == 2 && buttons.All(b => b.IsEffectivelyEnabled);
            var multi = lists.Count(m => m.HasFlag(Avalonia.Controls.SelectionMode.Multiple));
            dialog.Close();
            if (!usable) return Fail("the confirmation buttons are not usable");
            if (multi < 1) return Fail("no list in the manager accepts more than one row");
            Console.WriteLine($"manager  asked: {model.Manager.Asking.Title}");

            model.Manager.ProceedCommand.Execute(null);
            // The manager reports its own busy state; the shell is not involved in this one.
            WaitWhile(() => model.Manager.Busy || model.Manager.Asking is not null, 180_000);
            Console.WriteLine($"manager  {model.Manager.Status}");

            var mineNow = model.Manager.Mods.First(m => m.Mod.Id == PackIdentity);
            if (mineNow.Enabled) return Fail("the mod is still on after being turned off");
            if (model.Manager.Mods.Single(m => m.Mod.Id == PackIdentityB).Enabled is false)
                return Fail("turning one mod off also turned off the other");

            // Only this mod's own bundles. Anything else installed and still on is supposed to be
            // left written — asserting the whole game went vanilla passed only for as long as this
            // test happened to be the only thing installed.
            var mineBundles = mineNow.Mod.TouchedBundles.Keys.ToHashSet();
            if (model.Manager.Bundles.Any(b =>
                    mineBundles.Contains(b.Bundle)
                    && b.State == PGAssetTool.Core.Mods.BundleState.ChangedByThisTool))
                return Fail("turning it off left one of its own bundles changed");

            // The packs are kept beside the tool, so deleting a workspace cannot strand one.
            var kept = model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id))
                .Select(m => m.Mod.PackPath).ToList();
            Console.WriteLine($"manager  packs kept at {string.Join(", ", kept.Select(Path.GetFileName))}");
            if (kept.Any(p => !p.Contains("PGAssetTool-data")))
                return Fail("a pack was not copied into the store");

            // Now both, in one gesture. Removed through the manager rather than around it, so the
            // path a person actually takes is the one under test. This also puts the game back: it
            // is a test, not a change anyone asked for.
            foreach (var row in model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id)))
                model.Manager.Selection.Add(row);
            if (model.Manager.Chosen.Count != 2)
                return Fail($"two mods were selected and {model.Manager.Chosen.Count} are being acted on");

            model.Manager.RemoveCommand.Execute(null);
            if (model.Manager.Asking is null) return Fail("removing asked for no confirmation");
            Console.WriteLine($"manager  asked: {model.Manager.Asking.Title}");
            if (!model.Manager.Asking.Title.Contains("2 mods"))
                return Fail($"the confirmation does not say how many: {model.Manager.Asking.Title}");

            model.Manager.ProceedCommand.Execute(null);
            WaitWhile(() => model.Manager.Busy || model.Manager.Asking is not null, 180_000);

            Console.WriteLine($"manager  after removing both: {model.Manager.Status}");
            if (model.Manager.Mods.Any(m => mine.Contains(m.Mod.Id)))
                return Fail("a mod is still installed after both were removed");

            // Removing a mod deliberately leaves its kept pack, so reinstalling does not depend on
            // the workspace still existing. That is right for a person and wrong for a test that
            // runs on every change: twenty copies of one had piled up in the mods folder.
            foreach (var path in kept) if (File.Exists(path)) File.Delete(path);

            var left = model.Manager.Mods.Select(m => m.Mod.Id).Where(id => !wasInstalled.Contains(id)).ToList();
            Console.WriteLine($"manager  installed before this run: {wasInstalled.Count}, left behind by it: {left.Count}");
            if (left.Count > 0) return Fail($"this run left {string.Join(", ", left)} in the game");
            if (model.Status.Contains("Value cannot be null"))
                return Fail($"the shell reported an error during removal: {model.Status}");

            // Back to the workspace the edit was made in, which the batch moved off.
            model.Editor.SelectedWorkspace = model.Editor.Workspaces
                .FirstOrDefault(w => string.Equals(Path.GetFullPath(w.Directory), written,
                    StringComparison.OrdinalIgnoreCase));

            File.WriteAllBytes(texture.FullPath, bytes);
            model.Editor.Refresh();
            if (model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath).Edited)
                return Fail("putting the file back should clear the mark");

            var nowPreferences = File.Exists(theirSettings) ? File.ReadAllText(theirSettings) : null;
            Console.WriteLine("status   the author's settings file: "
                + (theirPreferences == nowPreferences ? "untouched" : "REWRITTEN"));
            if (theirPreferences != nowPreferences) return Fail($"this run rewrote {theirSettings}");

            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
        finally
        {
            model.Dispose();
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }

            // Also on the way out of a failed run. A run that stops before the manager still leaves
            // its pack in the store, and eighteen of them collected there while this test was being
            // fixed. Only files under this test's own name, which nothing else can be called.
            // Both spellings: a pack is filed under the mod's name now and was filed under its id
            // before, and a run that fails half way through either scheme still has to tidy up.
            var store = Path.Combine(ModStore.DefaultHome(), "mods");
            if (Directory.Exists(store))
                foreach (var pattern in new[] { PackName + "*.pgmod", PackIdentity + "*.pgmod" })
                foreach (var stale in Directory.GetFiles(store, pattern))
                    try { File.Delete(stale); } catch (IOException) { }
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

    /// Reads a bundle, puts the reader down, and asks whether the file is free.
    ///
    /// The whole design of the window rests on this: bundles are held open for browsing, and every
    /// write to the game closes the reader first and assumes that is enough. If disposing does not
    /// actually release the handle, an install works only until somebody has looked at the bundle it
    /// is about to write — which is intermittent, unattributable, and exactly what was happening.
    private static string? ReaderReleasesItsBundles(MainViewModel model)
    {
        foreach (var (what, pixels) in new[] { ("reading the objects", false), ("reading the pixels", true) })
        {
            const string name = "dw";
            string path;

            // Both halves, because they are different code. The pixels of a texture live in a
            // sibling stream inside the bundle, reached through the bundle's own data reader rather
            // than through the serialized file — which is the path a person takes by clicking a
            // texture, and the one nothing had ever checked released.
            using (var bundles = new BundleSet(model.Game!))
            {
                path = bundles.PathOf(name);
                var file = bundles.Open(name);
                var info = file.file.AssetInfos.First(i => i.TypeId == (int)AssetClassID.Texture2D);
                var field = bundles.Context.Deserialize(file, info)!;
                if (pixels && AssetPreview.Texture(bundles, name, field) is null)
                    return $"a texture in '{name}' could not be decoded";
            }

            try
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Console.WriteLine($"status   '{name}' released after {what}: yes");
            }
            catch (IOException ex)
            {
                return $"'{name}' is still held open after {what} — {ex.Message}";
            }
        }
        return null;
    }

    /// Renames a workspace and re-labels its manifest, the way the editor's form does.
    ///
    /// The id is set here rather than through the form, which deliberately does not offer it: the
    /// ledger keys an installed mod by it, so changing it in the window would turn an update into a
    /// second copy. This test needs its own, which is a different thing from an author needing one.
    private static string? Rename(
        MainViewModel model, string directory, string folder, string name, string id)
    {
        model.Editor.Rescan(model.WorkspaceRoot);

        var full = Path.GetFullPath(directory);
        model.Editor.SelectedWorkspace = model.Editor.Workspaces.FirstOrDefault(
            w => string.Equals(Path.GetFullPath(w.Directory), full, StringComparison.OrdinalIgnoreCase));
        if (model.Editor.SelectedWorkspace is null) return null;

        model.Editor.FolderName = folder;
        model.Editor.PackName = name;
        model.Editor.ApplyDetailsCommand.Execute(null);

        if (model.Editor.SelectedWorkspace is not { } moved) return null;
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(moved.Directory)), folder,
                StringComparison.Ordinal))
            return null;

        var renamed = Path.GetFullPath(Path.TrimEndingDirectorySeparator(moved.Directory));
        PGAssetTool.Core.Pack.Workspace.Save(
            renamed, PGAssetTool.Core.Pack.Workspace.Read(renamed) with { Id = id });
        return renamed;
    }

    /// How wide the first tile in the manager is actually being drawn, or null when there is none.
    private static double? Tile(Views.MainWindow window)
        => window.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .Select(i => i.FindAncestorOfType<Avalonia.Controls.Border>()?.Width)
            .FirstOrDefault(w => w is > 0);

    /// The pack shows a drawing of the model, and can be given another one from a different angle.
    ///
    /// The game's own weapon icon says which weapon a pack is for and nothing about what the pack
    /// does to it, which for a texture or mesh mod is the only interesting part. Both halves are
    /// checked: the one the export draws, and the one the editor's button takes from whatever the
    /// preview is showing.
    private static string? PackIconIsTheModel(MainViewModel model, string workspace)
    {
        var drawn = Path.Combine(workspace, PGAssetTool.Core.Preview.PackIcon.FileName);
        var manifest = PGAssetTool.Core.Pack.Workspace.Read(workspace);

        if (manifest.Icon != PGAssetTool.Core.Preview.PackIcon.FileName)
            return $"the pack shows '{manifest.Icon}' rather than a drawing of the model";
        if (!File.Exists(drawn)) return "the manifest names a drawing that was never made";

        // Disposed: leaving it open made the capture below fail to write over it, which is the file
        // being locked by the test rather than anything wrong with the tool.
        StbImageSharp.ImageInfo? size;
        using (var reading = File.OpenRead(drawn)) size = StbImageSharp.ImageInfo.FromStream(reading);
        Console.WriteLine($"editor   the export drew the model as {size?.Width}x{size?.Height}, "
            + $"{new FileInfo(drawn).Length:N0} bytes");
        if (size is not { Width: PGAssetTool.Core.Preview.PackIcon.Size })
            return "the drawing is not the size an icon is meant to be";

        // Drawn again from somewhere else, the way the button does it. A mesh read back off disk,
        // because that is what the editor is showing when somebody presses it.
        var glb = Directory.EnumerateFiles(workspace, "*.glb", SearchOption.AllDirectories).FirstOrDefault();
        if (glb is null) return null;

        if (AssetPreview.FromFile(glb) is not PGAssetTool.Core.Export.Meshes.UnityMesh mesh) return $"'{Path.GetFileName(glb)}' would not read back";

        var turned = PGAssetTool.Core.Preview.PackIcon.Render(
            mesh, null, new PGAssetTool.Core.Preview.Camera(Yaw: 2.1f, Pitch: -0.4f));
        if (PGAssetTool.Core.Preview.PackIcon.IsBlank(turned)) return "drawing it from another angle came out empty";

        var before = File.ReadAllBytes(drawn);
        model.Editor.CaptureIcon(turned);
        Console.WriteLine($"editor   {model.Editor.Status}");

        if (PGAssetTool.Core.Pack.Workspace.Read(workspace).Icon != PGAssetTool.Core.Preview.PackIcon.FileName)
            return $"capturing a view did not become the pack's icon: {model.Editor.Status}";
        if (File.ReadAllBytes(drawn).SequenceEqual(before))
            return "capturing a different angle left the old picture in place";

        return null;
    }

    /// Extracts another weapon under this test's own name, with one file edited so there is
    /// something to pack. Answers where it landed, or null when a step of that fell short.
    private static string? SecondWorkspace(
        MainViewModel model, int weapon, string folder, string id, string name)
    {
        if (!Select(model, weapon)) return null;

        model.ExtractWeaponCommand.Execute(null);
        WaitWhile(() => model.Busy, 120_000);
        if (model.LastExport is not { } exported || !Directory.Exists(exported)) return null;

        if (Rename(model, exported, folder, name, id) is not { } directory) return null;
        var manifest = PGAssetTool.Core.Pack.Workspace.Read(directory);

        // Packing keeps only what differs from the export, so an untouched workspace packs nothing.
        var texture = manifest.Operations.FirstOrDefault(o => o.Source.EndsWith(".png"));
        if (texture is null) return null;

        var path = Path.Combine(directory, texture.Source);
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. new byte[16]]);

        Console.WriteLine($"batch    a second workspace at {Path.GetFileName(directory)}, "
            + $"editing {texture.Source}");
        return directory;
    }

    private static bool Select(MainViewModel model, int number)
    {
        model.Selected = model.Weapons.First(w => w.Record.GameNumber == number);
        return WaitWhile(() => model.Detail?.Tree.Record.GameNumber != number, 60_000);
    }

    private static int CountRows(IEnumerable<TreeNode> nodes)
        => nodes.Sum(n => 1 + CountRows(n.Children));

    private static int Fail(string why)
    {
        Console.Error.WriteLine($"SELF TEST FAILED: {why}");
        return 1;
    }
}
