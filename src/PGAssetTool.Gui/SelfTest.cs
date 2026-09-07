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
            // Searched, so the reload has something to put back wrongly. A reload happens under
            // somebody in the middle of something — building a pack causes one — and it used to
            // hand back the whole list while the search box still said what they had typed.
            model.Search = "ultimatum";
            var narrowed = model.Weapons.Count;

            clock.Restart();
            // Blocking rather than awaiting: an await here would resume on a pool thread, and the
            // headless window can only be closed from the one that made it.
            Settle(model.ReloadAsync(), "reloading");
            WaitWhile(() => model.Busy, 120_000);
            Console.WriteLine($"reload   {clock.ElapsedMilliseconds}ms for the whole game, "
                + $"'{model.Search}' still showing {model.Weapons.Count} of {narrowed}");
            if (clock.ElapsedMilliseconds > 8000)
                return Fail($"reloading took {clock.ElapsedMilliseconds}ms");

            if (narrowed == 0) return Fail("searching for 'ultimatum' found nothing to reload with");
            if (model.Weapons.Count != narrowed)
                return Fail($"reloading put {model.Weapons.Count} weapons back where a search left {narrowed}");

            model.Search = "";

            // With a window open the list box owns the selection, and rebuilding the collection —
            // which searching and reloading both do — clears it. So pick one again before asking
            // for it to be written out.
            if (!Select(model, 16)) return Fail("selecting #16 resolved nothing");

            // A skin is worth a row only if what it changes can be reached from it.
            if (!Select(model, 416)) return Fail("selecting #416 resolved nothing");

            if (SkinsAreOffered(model, scratch) is { } skinProblem) return Fail(skinProblem);

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

            // Built protected once, so the whole loop is exercised on a signed pack rather than on
            // the plain one: what the manager reads, what the applier unpacks, and what the seal
            // says about it are three different code paths through the same file.
            model.ProtectPacks = true;
            Core.Pack.Workspace.Save(written, Core.Pack.Workspace.Read(written) with { Protect = null });

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

            if (TheViewOutlivesAReading(model, texture) is { } viewProblem) return Fail(viewProblem);

            if (TwoModelsKeepTheirOwnViews(model, texture) is { } swapProblem) return Fail(swapProblem);

            if (DroppedFilesLand(model, texture) is { } dropProblem) return Fail(dropProblem);

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

            // Adding an asset is the one operation with nothing on disk to notice, so it is written
            // into the manifest rather than made by editing a file. It goes in after the second
            // workspace is prepared and before anything is packed, so it travels the same road as
            // everything else: built, signed, installed, reconciled and removed.
            if (AddAnAsset(model, written, out var added) is { } addProblem) return Fail(addProblem);

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
                // One edited file each, and in the first workspace the two operations the addition
                // put there as well — those carry no baseline, because a file that is the change
                // has no earlier state to differ from.
                var packing = string.Equals(Path.GetFullPath(w.Directory), written,
                    StringComparison.OrdinalIgnoreCase) ? 3 : 1;

                var differ = PGAssetTool.Core.Pack.Workspace.Changed(
                    w.Directory, PGAssetTool.Core.Pack.Workspace.Read(w.Directory));
                Console.WriteLine($"batch    {w.Name}: {differ.Count} of {w.Files} files differ"
                    + (differ.Count > 0 ? $" ({string.Join(", ", differ.Select(o => o.Source))})" : ""));
                if (differ.Count != packing)
                    return Fail($"'{w.Name}' should be packing {packing} and is packing {differ.Count}");
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

            // The same install a pack dropped on the window takes: a file, straight to the applier,
            // with no workspace behind it. That is how somebody else's mod gets in, and it is the
            // one install path with nothing in front of it to catch a mistake.
            if (installed.FirstOrDefault(m => m.Id == PackIdentity)?.PackPath is not { } dropped)
                return Fail("the kept pack has no path to install from");

            _ = model.InstallPacks([dropped]);
            WaitWhile(() => model.Busy, 300_000);
            Console.WriteLine($"drop     installing '{Path.GetFileName(dropped)}' on its own: {model.Status}");

            if (!model.Status.Contains("applied") || model.Status.Contains("1 failed"))
                return Fail($"installing a pack by itself did not apply it: {model.Status}");
            if (new PGAssetTool.Core.Mods.ModStore(model.Game!).Read().All(m => m.Id != PackIdentity))
                return Fail("a pack installed on its own is not in the ledger");

            // Read out of the bundle the game will load, not out of what the applier said it did.
            if (AddedAssetIs(model, added!, installed: true) is { } addProblem2) return Fail(addProblem2);
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

                // A protected pack has to stay openable by this tool and stay shut to everything
                // else, and the manager has to say who built it. Checked on the pack the manager is
                // actually pointing at, not on one made for the occasion.
                var seal = Core.Pack.PackFile.Inspect(row.Mod.PackPath);
                Console.WriteLine($"manager  '{row.Name}' is {seal.Describe}");

                if (!seal.Protected) return Fail($"'{row.Name}' was built protected and is not");
                if (seal.State != Core.Pack.SealState.Signed)
                    return Fail($"'{row.Name}' does not verify against the key that signed it");

                try
                {
                    System.IO.Compression.ZipFile.OpenRead(row.Mod.PackPath).Dispose();
                    return Fail($"'{row.Name}' is protected and still opens as a zip");
                }
                catch (InvalidDataException) { }

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

            if (TheFormFitsASmallWindow(model) is { } tooTall) return Fail(tooTall);

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

            if (ModsAreFiledByWeaponAndLook(model) is { } filingProblem) return Fail(filingProblem);

            // Now both, in one gesture. Removed through the manager rather than around it, so the
            // path a person actually takes is the one under test. This also puts the game back: it
            // is a test, not a change anyone asked for.
            foreach (var row in model.Manager.Mods.Where(m => mine.Contains(m.Mod.Id)))
                model.Manager.Selection.Add(row);
            if (model.Manager.Chosen.Count != 2)
                return Fail($"two mods were selected and {model.Manager.Chosen.Count} are being acted on");

            // With shift held, which is what turns Remove into Remove and delete. Removing on its
            // own deliberately leaves the kept pack so reinstalling does not depend on the
            // workspace — right for a person, and wrong for a test that runs on every change:
            // twenty copies of one had piled up in the mods folder before this existed.
            model.Manager.ShiftHeld = true;
            if (model.Manager.RemoveLabel != "Remove and delete")
                return Fail($"with shift held the button still says '{model.Manager.RemoveLabel}'");

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

            // An added asset is only ever undone by the bundle being put back, so this is also the
            // check that removal really does restore rather than reverse each operation.
            if (AddedAssetIs(model, added!, installed: false) is { } addProblem3) return Fail(addProblem3);

            model.Manager.ShiftHeld = false;
            if (kept.Where(File.Exists).ToList() is { Count: > 0 } still)
                return Fail($"shift-removing left {string.Join(", ", still.Select(Path.GetFileName))} behind");
            Console.WriteLine($"manager  and the {kept.Count} kept pack file(s) went with them");

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

            // The workspace this run made a second time round is no longer wanted, which is the
            // case the button exists for. Deleted through it rather than around it, so the path a
            // person takes is the one under test — and it leaves this run's scratch behind it.
            if (WorkspacesCanBeDeleted(model, secondary) is { } deleteProblem) return Fail(deleteProblem);

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

    /// What the addition check put into the pack, and what it has to find afterwards.
    private sealed record Added(string Bundle, string ShaderName, long Material, long Shader);

    /// Puts an asset the bundle does not have into the pack, and points one it does have at it.
    ///
    /// Built here rather than committed, because a fixture for this would be an asset out of the
    /// player's own game and those are not this repository's to carry. It is also the only way to
    /// get one that matches the installation being written to.
    ///
    /// The added shader is a copy of the one the material already uses, renamed. That makes the
    /// whole mod a no-op by construction — the material ends up drawn by the same shader it was
    /// drawn by — while still exercising every part that is new: an addition, a name that would
    /// otherwise collide, a path id that is already taken, and an existing asset repointed at
    /// something that did not exist when the pack was built.
    private static string? AddAnAsset(MainViewModel model, string workspace, out Added? added)
    {
        added = null;
        var manifest = Core.Pack.Workspace.Read(workspace);
        if (manifest.Operations.Count == 0) return "the workspace names nothing to work from";

        using var bundles = new Core.Assets.BundleSet(model.Game!);
        var bundle = manifest.Operations[0].Target.Container;
        var file = bundles.Open(bundle);

        // A material pointing at a shader in its own file. Everything else about which one is
        // arbitrary, so the first is as good as any.
        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetsTools.NET.Extra.AssetClassID.Material) continue;

            var material = bundles.Context.Deserialize(file, info);
            if (material?["m_Shader"] is not { IsDummy: false } pointer) continue;
            if (pointer["m_FileID"].AsInt != 0) continue;

            var shaderId = pointer["m_PathID"].AsLong;
            if (file.file.GetAssetInfo(shaderId) is not { } shaderInfo) continue;
            if (shaderInfo.TypeId != (int)AssetsTools.NET.Extra.AssetClassID.Shader) continue;

            var name = PackIdentity + "-shader";
            var shaderFile = Path.Combine(workspace, "selftest-added-shader.dat");
            var materialFile = Path.Combine(workspace, "selftest-material.dat");
            File.WriteAllBytes(shaderFile, Core.Export.AssetExporter.ReadRaw(file, shaderInfo));
            File.WriteAllBytes(materialFile, Core.Export.AssetExporter.ReadRaw(file, info));

            var index = new Core.Assets.ContainerIndex(bundles.Context);
            var target = index.AddressOf(bundle, file, info, material["m_Name"].AsString);

            Core.Pack.Workspace.Save(workspace, manifest with
            {
                Operations =
                [
                    .. manifest.Operations,
                    new Core.Pack.PackOperation
                    {
                        Op = Core.Pack.PackOperations.AddAsset,
                        // Deliberately the id the copied shader already occupies, so the applier has
                        // to notice it is taken and hand out another.
                        Target = new Core.Assets.AssetAddress(
                            bundle, nameof(AssetsTools.NET.Extra.AssetClassID.Shader), name,
                            PathId: shaderId),
                        Source = Path.GetFileName(shaderFile),
                        NewId = name,
                    },
                    new Core.Pack.PackOperation
                    {
                        Op = Core.Pack.PackOperations.ReplaceRaw,
                        Target = target,
                        Source = Path.GetFileName(materialFile),
                        Pointers = [new Core.Pack.PointerFixup { Path = "m_Shader", NewId = name }],
                    },
                ],
            });

            added = new Added(bundle, name, info.PathId, shaderId);
            Console.WriteLine($"add      '{name}' into {bundle}, and "
                + $"{target} repointed at it (its own id {shaderId} is taken)");
            return null;
        }

        return $"no material in '{bundle}' points at a shader beside it";
    }

    /// What the added asset has to look like in the game once the pack is applied, and once it is
    /// removed again. `installed` says which of the two is being checked.
    private static string? AddedAssetIs(MainViewModel model, Added added, bool installed)
    {
        using var bundles = new Core.Assets.BundleSet(model.Game!);
        var file = bundles.Open(added.Bundle);

        var found = file.file.AssetInfos
            .Where(a => a.TypeId == (int)AssetsTools.NET.Extra.AssetClassID.Shader)
            .Where(a => Core.Assets.AssetNaming.NameOf(
                bundles.Context.Deserialize(file, a), AssetsTools.NET.Extra.AssetClassID.Shader)
                    == added.ShaderName)
            .ToList();

        if (!installed)
        {
            if (found.Count > 0)
                return $"'{added.ShaderName}' is still in {added.Bundle} after the mod was removed";
            Console.WriteLine($"add      removed: '{added.ShaderName}' is gone from {added.Bundle}");
        }
        else
        {
            if (found.Count != 1)
                return $"{found.Count} shaders in {added.Bundle} are called '{added.ShaderName}', not one";
        }

        var material = bundles.Context.Deserialize(file, file.file.GetAssetInfo(added.Material)!)!;
        var points = material["m_Shader"]["m_PathID"].AsLong;

        if (installed)
        {
            if (points != found[0].PathId)
                return $"the material points at {points}, not at the added shader {found[0].PathId}";
            if (points == added.Shader)
                return "the added shader got the id it asked for, which was already taken";

            Console.WriteLine($"add      installed: '{added.ShaderName}' is {found[0].PathId} "
                + $"(asked for {added.Shader}), and the material points at it");
        }
        else if (points != added.Shader)
        {
            return $"the material points at {points} rather than back at {added.Shader}";
        }

        return null;
    }

    /// An angle and a texture stay on the model when the same file is read again, and the two
    /// halves of the comparison move together.
    ///
    /// The workspace is re-read whenever anything in it is written — saving the pack's own icon
    /// counts, and so does anything an image editor does — and each reading empties the lists the
    /// panes are bound to on its way past, which used to take the view with it. That put the angle
    /// back to square at the exact moment somebody had turned the model to the one they wanted.
    private static string? TheViewOutlivesAReading(MainViewModel model, Core.Pack.WorkspaceFile back)
    {
        if (model.Editor.Files.FirstOrDefault(f => f.Name.EndsWith(".glb")) is not { } model3d)
            return "no model in the extracted workspace to look at";

        model.Editor.SelectedFile = model3d;
        WaitWhile(() => model.Editor.Edited.Mesh is null, 60_000);
        if (model.Editor.Edited.Mesh is null) return "the file side of the comparison shows no model";

        if (model.Editor.Edited.TextureChoices.FirstOrDefault(c => c.PathId == -1) is not { } wearing)
            return "the editor offered none of the workspace's own textures to put on the model";

        var turned = new Camera(Yaw: 2.1f, Pitch: -0.4f, Distance: 0.9f);
        model.Editor.Edited.Camera = turned;
        model.Editor.Edited.ChosenTexture = wearing;

        if (model.Editor.Original.Camera != turned)
            return "the halves are linked, and the game side did not follow the view";
        if (model.Editor.Original.ChosenTexture != wearing)
            return "the halves are linked, and the game side did not take the same texture";

        // The reading a save sets off, which is where the view used to be lost.
        model.Editor.Refresh();
        WaitWhile(() => model.Editor.Edited.Mesh is null, 60_000);

        if (model.Editor.Edited.Camera != turned)
            return $"reading the model again threw the view away: {model.Editor.Edited.Camera}";
        if (model.Editor.Edited.ChosenTexture != wearing)
            return "reading the model again took the texture off the model";
        if (model.Editor.Edited.MeshTextures is null)
            return "the texture was still the chosen one but no longer on the model";

        // Away and back is the same asset as far as the view is concerned, and a different asset
        // is not: an angle chosen for one model says nothing about the next.
        model.Editor.SelectedFile = back;
        WaitWhile(() => model.Editor.Edited.Nothing is not null, 60_000);
        model.Editor.SelectedFile = model3d;
        WaitWhile(() => model.Editor.Edited.Mesh is null, 60_000);

        if (model.Editor.Edited.Camera != turned)
            return "looking at something else and coming back lost the view";

        // Unlinked they are free to differ, which is what a replaced mesh needs: the two are then
        // different models, and each is worth looking at on its own terms.
        model.Editor.Linked = false;
        model.Editor.Edited.Camera = turned.Zoomed(1.5f);
        if (model.Editor.Original.Camera != turned)
            return "with the link off, one half still dragged the other along";

        model.Editor.Linked = true;
        if (model.Editor.Original.Camera != model.Editor.Edited.Camera)
            return "linking them again left them looking from different places";

        Console.WriteLine("editor   the view survives a reading, and the halves move together");
        model.Editor.SelectedFile = back;
        WaitWhile(() => model.Editor.Edited.Nothing is not null, 60_000);
        return null;
    }

    /// A kept pack goes in the folder its weapon and its look name, and the manager shows the same
    /// arrangement.
    ///
    /// Two views of one thing that disagree are worse than either, so the check is that they do
    /// not: the folder the file is in, and the folder the tree opens, are the same two names.
    /// The folder standing at a path, wherever in the shelf it ended up.
    private static ModFolder? Folder(MainViewModel model, IReadOnlyList<string> at)
    {
        var found = new List<ModFolder>();
        void Walk(ModFolder node)
        {
            found.Add(node);
            foreach (var child in node.Children) Walk(child);
        }

        foreach (var root in model.Manager.Folders) Walk(root);

        return found.FirstOrDefault(f => f.At.SequenceEqual(at, StringComparer.OrdinalIgnoreCase));
    }

    private static string? ModsAreFiledByWeaponAndLook(MainViewModel model)
    {
        if (model.Manager.Mods.FirstOrDefault(m => m.Mod.Id == PackIdentity) is not { } row)
            return $"the manager does not list '{PackIdentity}'";
        if (row.Mod.Subject is not { } subject)
            return "the ledger did not record what the mod is for";

        var under = Path.Combine([.. subject.Path]);
        Console.WriteLine($"manager  '{row.Name}' is filed under {under.Replace('\\', '/')}");

        if (!row.Mod.PackPath.Contains(under, StringComparison.OrdinalIgnoreCase))
            return $"it is kept at {row.Mod.PackPath}, which is not under {under}";
        if (!File.Exists(row.Mod.PackPath))
            return "the ledger names a kept pack that is not there";

        // Found by the path rather than by depth. The kind is the top level of the arrangement and
        // the shelf leaves it out while everything installed is the same kind, so how deep any of
        // these sit depends on what else is installed.
        if (Folder(model, subject.Path.Take(2).ToList()) is not { } weapon)
            return "the manager's shelf has no folder for the weapon this mod is for";
        if (Folder(model, subject.Path) is not { } look)
            return $"'{weapon.Label}' has no folder for the look this mod changes";

        // Opening a folder narrows the tiles to what it holds, and to nothing else.
        model.Manager.Folder = look;
        Console.WriteLine($"manager  {weapon.Label} / {look.Label} shows {model.Manager.Shown.Count} "
            + $"of {model.Manager.Mods.Count} installed");

        if (model.Manager.Shown.All(r => r.Mod.Id != PackIdentity))
            return "opening the folder hid the mod filed in it";
        if (model.Manager.Shown.Any(r => r.Mod.Id == PackIdentityB))
            return "the folder shows a mod filed under another weapon";

        // Searched the way the weapon list is searched, which is what makes a weapon findable by
        // any of its names — the number is the plainest proof that it is the same search.
        model.Manager.FolderSearch = subject.Number.ToString();
        if (model.Manager.Shown.All(r => r.Mod.Id != PackIdentity))
            return $"searching for #{subject.Number} hid the mod that is for it";
        if (model.Manager.Shown.Any(r => r.Mod.Id == PackIdentityB))
            return $"searching for #{subject.Number} showed a mod for another weapon";

        model.Manager.FolderSearch = "nothing-installed-is-called-this";
        if (model.Manager.Shown.Count > 0)
            return "a search that matches nothing still showed something";

        model.Manager.FolderSearch = "";

        // Back to everything, which is what the rest of this run acts on.
        model.Manager.Folder = model.Manager.Folders.FirstOrDefault();
        if (model.Manager.Shown.Count != model.Manager.Mods.Count)
            return "the top of the shelf does not hold everything installed";

        // The headings say what the game calls these things now, not what they were called when
        // the pack was built. A pack made in English must not go on reading as English to somebody
        // working in Japanese — and a pack made before any of this existed carries no name to
        // translate at all, so the lookup is by number and by skin id.
        string? Heading() => Folder(model, subject.Path.Take(2).ToList())?.Label;

        var english = Heading();
        var was = model.Language;
        model.Language = "l_ja";
        var japanese = Heading();
        model.Language = was;

        Console.WriteLine($"manager  the heading reads '{english}' and '{japanese}' in Japanese");
        if (japanese is null) return "the shelf lost the weapon when the language changed";
        if (japanese == english)
            return $"the heading stayed '{english}' when the language changed";
        if (Heading() != english) return "the heading did not come back when the language did";

        return null;
    }

    /// A workspace can be thrown away from the editor, folder and all, and it asks first.
    private static string? WorkspacesCanBeDeleted(MainViewModel model, string directory)
    {
        model.Editor.Rescan(model.WorkspaceRoot);
        model.Editor.Selection.Clear();

        model.Editor.SelectedWorkspace = model.Editor.Workspaces.FirstOrDefault(w =>
            string.Equals(Path.GetFullPath(w.Directory), Path.GetFullPath(directory),
                StringComparison.OrdinalIgnoreCase));
        if (model.Editor.SelectedWorkspace is null)
            return $"the editor no longer lists {Path.GetFileName(directory)} to delete";

        model.Editor.DeleteCommand.Execute(null);
        if (model.Editor.Asking is null) return "deleting a workspace asked for no confirmation";
        Console.WriteLine($"editor   asked: {model.Editor.Asking.Title}");

        model.Editor.ProceedCommand.Execute(null);
        WaitWhile(() => model.Editor.Asking is not null, 30_000);
        Console.WriteLine($"editor   {model.Editor.Status}");

        if (Directory.Exists(directory)) return $"{Path.GetFileName(directory)} is still on disk";
        if (model.Editor.Workspaces.Any(w =>
                string.Equals(Path.GetFullPath(w.Directory), Path.GetFullPath(directory),
                    StringComparison.OrdinalIgnoreCase)))
            return "the deleted workspace is still in the list";

        return null;
    }

    /// Going back and forth between two models leaves each one at its own angle.
    ///
    /// Each pane remembers a view per model, and the two panes hand their view to each other while
    /// they are linked — which they must not do while one of them still holds the model before
    /// last. Doing it anyway wrote one model's angle into the other's memory, so walking between
    /// two meshes carried a view across, back, and across again, and everything came round to
    /// where it started every few passes. Three passes, because one shows nothing.
    private static string? TwoModelsKeepTheirOwnViews(MainViewModel model, Core.Pack.WorkspaceFile back)
    {
        var meshes = model.Editor.Files.Where(f => f.Name.EndsWith(".glb")).Take(2).ToList();
        if (meshes.Count < 2) return "the workspace has only one model, and this needs two";

        Camera[] views =
        [
            new(Yaw: 1.1f, Pitch: 0.2f, Distance: 1.3f),
            new(Yaw: -0.6f, Pitch: -0.9f, Distance: 2.2f),
        ];

        for (var i = 0; i < 2; i++)
        {
            model.Editor.SelectedFile = meshes[i];
            WaitWhile(() => model.Editor.Edited.Mesh is null, 60_000);
            if (model.Editor.Edited.Mesh is null) return $"'{meshes[i].Name}' would not show";

            model.Editor.Edited.Camera = views[i];
        }

        for (var pass = 1; pass <= 3; pass++)
            for (var i = 0; i < 2; i++)
            {
                model.Editor.SelectedFile = meshes[i];
                WaitWhile(() => model.Editor.Edited.Mesh is null, 60_000);

                if (model.Editor.Edited.Camera != views[i])
                    return $"on pass {pass} '{meshes[i].Name}' came back at "
                        + $"{model.Editor.Edited.Camera}, not {views[i]}";
                if (model.Editor.Linked && model.Editor.Original.Camera != views[i])
                    return $"on pass {pass} the two halves of '{meshes[i].Name}' disagree";
            }

        Console.WriteLine($"editor   '{meshes[0].Name}' and '{meshes[1].Name}' kept their own views "
            + "across three passes");

        model.Editor.SelectedFile = back;
        WaitWhile(() => model.Editor.Edited.Nothing is not null, 60_000);
        return null;
    }

    /// Files dropped on the window reach the file in the workspace they were meant for.
    ///
    /// Three answers to "which one did you mean": the name says so, the selection says so, or
    /// nothing does — and the third has to be said out loud rather than guessed at, because every
    /// one of these overwrites a file somebody is working on.
    private static string? DroppedFilesLand(MainViewModel model, Core.Pack.WorkspaceFile texture)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "pgassettool-selftest-drop");
        Directory.CreateDirectory(scratch);

        try
        {
            // Named the way the workspace names it, which is how a file comes back from an editor
            // that was pointed straight at it.
            var painted = File.ReadAllBytes(texture.FullPath).Concat(new byte[32]).ToArray();
            var byName = Path.Combine(scratch, texture.Name);
            File.WriteAllBytes(byName, painted);

            model.Editor.Import([byName]);
            if (!File.ReadAllBytes(texture.FullPath).SequenceEqual(painted))
                return $"a file dropped under the workspace's own name did not land: {model.Editor.Status}";

            // Named whatever the editor felt like, with the file it is meant for selected.
            var again = painted.Concat(new byte[8]).ToArray();
            var byChoice = Path.Combine(scratch, "whatever-my-editor-called-it.png");
            File.WriteAllBytes(byChoice, again);

            model.Editor.SelectedFile = model.Editor.Files.Single(f => f.RelativePath == texture.RelativePath);
            WaitWhile(() => model.Editor.Edited.Nothing is not null, 60_000);

            model.Editor.Import([byChoice]);
            if (!File.ReadAllBytes(texture.FullPath).SequenceEqual(again))
                return $"a file dropped under another name did not reach the selected one: {model.Editor.Status}";

            // A kind of thing this workspace has nothing of, so there is no honest answer.
            var stranger = Path.Combine(scratch, "nothing-here-is-called-this.glb");
            File.WriteAllBytes(stranger, painted);

            model.Editor.Import([stranger]);
            if (!model.Editor.Status.Contains("not taken"))
                return $"a file with nothing to replace was not refused: {model.Editor.Status}";

            Console.WriteLine($"drop     '{texture.Name}' taken by name and by selection; "
                + $"a .glb with nothing to replace refused ({model.Editor.Status.Trim()})");
            return null;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
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

    /// The pack details form keeps its buttons on the page in a window squeezed to its smallest.
    ///
    /// It did not: the form is docked to the bottom of a column, and a window short enough simply
    /// clipped it — Save and Revert went off the bottom with no way to reach them. Measured at the
    /// smallest size the window will go to, because that is the case that failed.
    private static string? TheFormFitsASmallWindow(MainViewModel model)
    {
        var showing = model.Workspace;
        var window = new Views.MainWindow { DataContext = model, Width = 900, Height = 560 };
        window.Show();
        model.Workspace = MainViewModel.EditorTab;

        // The form is behind an expander, which builds nothing until it is opened.
        foreach (var expander in window.GetVisualDescendants().OfType<Avalonia.Controls.Expander>())
            expander.IsExpanded = true;

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Avalonia.Size(900, 560));
        window.Arrange(new Avalonia.Rect(0, 0, 900, 560));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.Content is "Save" or "Revert")
            .ToList();

        var offscreen = buttons
            .Select(b => b.TranslatePoint(new Avalonia.Point(0, b.Bounds.Height), window))
            .Count(p => p is null || p.Value.Y > 560);

        Console.WriteLine($"editor   at 900x560 the form's {buttons.Count} buttons are on the page: "
            + $"{buttons.Count - offscreen} of {buttons.Count}");

        window.Close();

        // Put the tab back: the caller is part way through the manager, and this borrowed the model
        // to look at the editor.
        model.Workspace = showing;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        if (buttons.Count != 2) return $"the pack details form shows {buttons.Count} buttons, not two";
        return offscreen > 0 ? $"{offscreen} of the form's buttons fall off a 900x560 window" : null;
    }

    /// How wide the first tile in the manager is actually being drawn, or null when there is none.
    private static double? Tile(Views.MainWindow window)
        => window.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .Select(i => i.FindAncestorOfType<Avalonia.Controls.Border>()?.Width)
            .FirstOrDefault(w => w is > 0);

    /// A skin can be written out alongside the weapon, models and all.
    ///
    /// Skin assets were left out of every export until now: the tree resolved them and the exporter
    /// was never taught to walk them, so the one thing most weapon mods change could not be reached.
    /// Both kinds are checked, because they are found by different means — most skins repaint the
    /// same geometry, and a few bring a model of their own filed under the skin's id.
    private static string? SkinsAreOffered(MainViewModel model, string scratch)
    {
        var offered = model.SkinChoices.Where(s => s.Id is not null).ToList();
        Console.WriteLine($"skins    {offered.Count} offered, "
            + $"{offered.Count(s => s.HasModel)} of them with a model of their own");

        if (offered.Count == 0) return "#416 has skins and none was offered for extraction";
        if (!model.HasSkins) return "skins were offered and the picker is hidden";
        if (model.ChosenSkin?.Id is not null) return "a skin was chosen before anybody asked for one";

        var withModel = offered.FirstOrDefault(s => s.HasModel);
        if (withModel is null) return "#416 has a skin with its own model and none was marked as having one";

        // Through the shell's own command, so the picker, the exporter and the folder naming are
        // all under test rather than only the last of them.
        var written = new List<string>();
        foreach (var skin in new[] { offered.First(s => !s.HasModel), withModel })
        {
            model.ChosenSkin = skin;
            model.ExtractWeaponCommand.Execute(null);
            WaitWhile(() => model.Busy, 300_000);

            if (model.LastExport is not { } directory || !Directory.Exists(directory))
                return $"extracting '{skin.Name}' wrote nothing: {model.Status}";
            written.Add(directory);

            var mine = Path.Combine(directory, "skin");
            if (!Directory.Exists(mine)) return $"'{skin.Name}' was asked for and nothing of it was written";

            var files = Directory.GetFiles(mine, "*", SearchOption.AllDirectories);
            Console.WriteLine($"skins    '{skin.Name}' -> {Path.GetFileName(directory)}, "
                + $"{files.Length} files of its own ("
                + string.Join(", ", files.GroupBy(f => Path.GetExtension(f))
                    .Select(g => $"{g.Count()}{g.Key}")) + ")");

            if (!files.Any(f => f.EndsWith(".png")))
                return $"'{skin.Name}' wrote no texture, which is the whole of what a skin is";
            if (skin.HasModel && !files.Any(f => f.EndsWith(".glb")))
                return $"'{skin.Name}' brings its own model and no mesh was written";
            if (!skin.HasModel && files.Any(f => f.EndsWith(".glb")))
                return $"'{skin.Name}' only repaints and a mesh was written for it";

            // Nothing of the other skins. Every skin's offer icon, profile and definition is filed
            // against the same weapon, so all of them used to come out whichever one was asked for
            // — seven sets of files an author has no reason to touch to change the eighth.
            var alongside = Path.Combine(directory, "related");
            var others = (Directory.Exists(alongside)
                    ? Directory.GetFiles(alongside, "*", SearchOption.AllDirectories)
                    : [])
                .Where(f => !Path.GetFileName(f).StartsWith(skin.Id!, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count > 0)
                return $"'{skin.Name}' brought {others.Count} file(s) belonging to other skins: "
                    + string.Join(", ", others.Select(Path.GetFileName));

            // A skin with its own model replaces the weapon rather than repainting it, so the
            // weapon's own geometry is not what is being changed and has no business being here.
            var weaponsOwn = Path.Combine(directory, "meshes");
            if (skin.HasModel && Directory.Exists(weaponsOwn))
                return $"'{skin.Name}' has a model of its own and the weapon's was written too";
            if (!skin.HasModel && !Directory.Exists(weaponsOwn))
                return $"'{skin.Name}' only repaints and the model it repaints was not written";
        }

        // Asked for again, the same skin gets a workspace of its own rather than writing over the
        // one that is already there — which used to take an author's edits with it, and the
        // manifest that said which mod their work was.
        model.ChosenSkin = withModel;
        model.ExtractWeaponCommand.Execute(null);
        WaitWhile(() => model.Busy, 300_000);

        if (model.LastExport is not { } again || !Directory.Exists(again))
            return $"extracting '{withModel.Name}' a second time wrote nothing: {model.Status}";
        if (string.Equals(Path.GetFullPath(again), Path.GetFullPath(written[1]),
                StringComparison.OrdinalIgnoreCase))
            return $"extracting '{withModel.Name}' twice wrote over the first at {again}";

        Console.WriteLine($"skins    asked for '{withModel.Name}' again -> {Path.GetFileName(again)}");
        written.Add(again);

        // Two looks of one weapon are two mods.
        //
        // The id used to be the weapon's slug and nothing else, so every mod of a weapon was the
        // same mod as far as the ledger went: installing a skin replaced the plain one, and
        // installing somebody else's replaced yours — quietly, since replacing by id is how a mod
        // is updated. Each extraction mints its own now, and says what it was made from.
        var manifests = written.Select(Core.Pack.Workspace.Read).ToList();
        Console.WriteLine($"skins    filed as {string.Join(" and ", manifests.Select(m => m.Id))}");

        if (manifests[0].Id == manifests[1].Id)
            return $"two skins of one weapon were both called '{manifests[0].Id}'";

        // Including the two of the same skin: two extractions are two mods whatever they were made
        // from, which is what "mints its own" has to mean to be worth anything.
        if (manifests.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifests.Count)
            return $"{manifests.Count} extractions produced fewer ids than that";

        foreach (var manifest in manifests)
        {
            if (manifest.Subject is not { } subject)
                return $"'{manifest.Id}' does not say which weapon it is for";
            if (subject.Number != 416)
                return $"'{manifest.Id}' says it is for #{subject.Number}, not the weapon it came from";
            if (subject.Variant.Length == 0)
                return $"'{manifest.Id}' was extracted with a skin and records none";
        }

        // Same kind, same weapon, different look: the first two levels of the path agree and the
        // last one does not.
        if (!manifests[0].Subject!.Path.Take(2).SequenceEqual(manifests[1].Subject!.Path.Take(2)))
            return "two skins of one weapon are filed under two different weapons";
        if (manifests[0].Subject!.Path[2] == manifests[1].Subject!.Path[2])
            return "two different skins are filed under the same look";

        // Put it back, or extracting the next weapon would carry this one's skin along with it.
        model.ChosenSkin = SkinChoice.None;
        foreach (var directory in written) Directory.Delete(directory, recursive: true);
        model.Editor.Rescan(model.WorkspaceRoot);
        return null;
    }

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
