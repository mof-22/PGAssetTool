using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Game;
using Avalonia.Media.Imaging;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Gui.ViewModels;

/// An installed mod as a row.
public sealed record InstalledRow(InstalledMod Mod)
{
    public string Name => Mod.Name.Length > 0 ? Mod.Name : Mod.Id;
    public bool Enabled => Mod.Enabled;

    public string Detail =>
        $"{Mod.Id}   {Mod.Version}   {Mod.TouchedBundles.Count} bundle(s)"
        + $"   installed {Mod.InstalledAt:yyyy-MM-dd} for {Mod.GameVersion}";

    /// The picture the pack carries, read out of it once.
    ///
    /// A list of installed mods is a list of names, and a name is a poor way to tell one weapon
    /// re-skin from another when several are installed. Read lazily because most of them are never
    /// looked at, and held afterwards because the rows are rebuilt on every refresh.
    public Bitmap? Icon => _icon ??= Load();

    private Bitmap? _icon;

    private Bitmap? Load()
    {
        try
        {
            if (Mod.PackPath.Length == 0 || !File.Exists(Mod.PackPath)) return null;
            if (PackBuilder.ReadIcon(Mod.PackPath) is not { Length: > 0 } bytes) return null;
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    public bool HasIcon => Icon is not null;
}

/// What a pack says about itself, read out of the manifest inside it.
///
/// The ledger keeps only what the tool needs to undo an install. Everything an author wrote — what
/// the mod is called, who made it, what it says it does, and which assets it replaces — lives in
/// the pack, and is worth showing to whoever is deciding whether to keep it.
public sealed record ModDetails(
    string Name, string Id, string Author, string Version, string Description,
    string BuiltAgainst, string Pack, IReadOnlyList<string> Replaces, PackSeal Seal)
{
    /// Said plainly and left at that. A pack somebody altered still installs — running a mod you
    /// changed yourself is nobody else's business — but it no longer passes for the one its author
    /// built, and the person about to install it is the one who should know.
    public string Signature => Seal.Describe;

    public bool WasAltered => Seal.State == SealState.Altered;

    public bool HasDescription => Description.Length > 0;
    public string Summary => $"{Version}   by {(Author.Length > 0 ? Author : "nobody in particular")}";

    public string Built => BuiltAgainst.Length > 0
        ? $"built against game {BuiltAgainst}"
        : "built against an unrecorded game version";

    public string Changes => Replaces.Count == 1 ? "Replaces one asset" : $"Replaces {Replaces.Count} assets";

    public static ModDetails? Read(InstalledMod mod)
    {
        try
        {
            var manifest = PackBuilder.ReadManifest(mod.PackPath);
            return new ModDetails(
                manifest.Name, manifest.Id, manifest.Author, manifest.Version, manifest.Description,
                manifest.BuiltAgainstGameVersion ?? "", mod.PackPath,
                manifest.Operations
                    .Select(o => $"{Verb(o.Op)}  {o.Target.Name}  ({o.Target.Class} in {o.Target.Container})")
                    .ToList(),
                PackFile.Inspect(mod.PackPath));
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            // The pack is gone or unreadable, which the manager says elsewhere; there is simply
            // nothing to show here.
            return null;
        }
    }

    private static string Verb(string op) => op switch
    {
        PackOperations.ReplaceTexture => "texture",
        PackOperations.ReplaceMesh => "mesh",
        PackOperations.ReplaceAudio => "sound",
        _ => op,
    };
}

/// What a request needs confirming before it happens.
public sealed record Confirmation(string Title, string Body, Func<Task> Proceed);

/// The game side: what is installed, what state the bundles are in, and turning things on and off.
///
/// Everything here writes to the game, which is the one thing the rest of the tool never does. So
/// it says what it is about to do first, and it always checks whether the game is running —
/// writing a bundle out from under it is what leaves the half-written files that make people
/// distrust modding tools.
public sealed partial class ManagerViewModel : ObservableObject
{
    private readonly Func<GameInstallation?> _game;

    public ManagerViewModel(Func<GameInstallation?> game) => _game = game;

    public ObservableCollection<InstalledRow> Mods { get; } = [];
    public ObservableCollection<BundleReport> Bundles { get; } = [];

    [ObservableProperty] private InstalledRow? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _backups = "";
    [ObservableProperty] private bool _gameIsRunning;

    /// Set while the game is being rewritten. Nothing else here is slow enough to matter.
    [ObservableProperty] private bool _busy;

    /// Set when something is waiting on an answer. The view shows it; nothing happens until it is
    /// resolved either way.
    [ObservableProperty] private Confirmation? _asking;

    /// Skips the confirmation for routine changes. Never skips the warning about the game running.
    [ObservableProperty] private bool _confirmChanges = true;

    /// What the selected pack says about itself, read from the pack rather than from the ledger.
    [ObservableProperty] private ModDetails? _details;

    partial void OnSelectedChanged(InstalledRow? value) => Details = value is null ? null : ModDetails.Read(value.Mod);

    /// How big a tile is drawn, in pixels down one side.
    ///
    /// Worked with Ctrl and the wheel, because how many mods fit on the page against how well each
    /// one can be made out is a judgement that changes with how many are installed — and with how
    /// close somebody is looking.
    [ObservableProperty] private int _tileSize = 112;

    /// The tile itself is a little wider than its picture, to leave the name somewhere to sit.
    public int TileWidth => TileSize + 12;

    partial void OnTileSizeChanged(int value) => OnPropertyChanged(nameof(TileWidth));

    public const int SmallestTile = 56;
    public const int LargestTile = 224;

    /// One notch of the wheel. Proportional rather than fixed: the same step feels like a lot at
    /// the small end and like nothing at the large one.
    public void ResizeTiles(int notches)
        => TileSize = Math.Clamp(
            (int)Math.Round(TileSize * Math.Pow(1.15, notches)), SmallestTile, LargestTile);

    /// Runs the work with the reader put down and picks it up again afterwards.
    ///
    /// The shell holds the bundles open for browsing, and everything here rewrites those same
    /// files — a write attempted around a live reader fails with the file in use, which is exactly
    /// what happened as soon as the confirmation started working.
    public Func<Func<Task>, Task>? Around { get; set; }


    /// Reads only the bundles a mod claims. Finding one changed by something else means hashing all
    /// 2300 of them, which is a deliberate act rather than the cost of opening a tab.
    public void Refresh() => Load(everything: false);

    [RelayCommand]
    private void CheckEverything() => Load(everything: true);

    private void Load(bool everything)
    {
        if (_game() is not { } game) { Status = "The game is not open."; return; }

        var store = new ModStore(game);
        store.TidyKeptPackNames();
        var installed = store.Read();

        // Rebuilt rows are new instances; anything held from the previous set is stale.
        Selection.Clear();
        Mods.Clear();
        foreach (var mod in installed.OrderBy(m => m.InstalledAt)) Mods.Add(new InstalledRow(mod));

        Bundles.Clear();
        foreach (var report in InstallationReport.Read(game, store, everything)) Bundles.Add(report);

        GameIsRunning = GameProcess.IsRunning(game);
        Backups = store.BackupRoot;

        var elsewhere = Bundles.Count(b => b.State == BundleState.ChangedBySomethingElse);
        Status = $"{installed.Count} installed, {installed.Count(m => m.Enabled)} on"
            + (elsewhere > 0 ? $" — {elsewhere} bundle(s) changed by something outside this tool" : "")
            + (GameIsRunning ? " — the game is running" : "")
            + (everything ? "" : " — only the bundles mods claim were checked");
    }


    [RelayCommand]
    private void Enable() => Ask("Turn on", ids => Applier().SetEnabled(ids, true));

    [RelayCommand]
    private void Disable() => Ask("Turn off", ids => Applier().SetEnabled(ids, false));

    [RelayCommand]
    private void Remove() => Ask("Remove", ids => Applier().Remove(ids));

    [RelayCommand]
    private void Reapply() => Ask("Reapply everything", _ => Applier().Reconcile(), needsSelection: false);

    private ModApplier Applier() => new(_game()!, new ModStore(_game()!));

    /// The rows a command acts on: everything highlighted, or the one current row.
    ///
    /// Turning six mods off one at a time means six confirmations and six whole-game rebuilds, and
    /// leaves the game in five intermediate states nobody asked for.
    public IReadOnlyList<InstalledMod> Chosen =>
        Selection.Count > 0 ? Selection.Select(r => r.Mod).ToList()
        : Selected is { } row ? [row.Mod]
        : [];

    /// Bound to the list's own selection, which is where multiple rows live; Selected stays the
    /// anchor the details pane follows.
    public ObservableCollection<InstalledRow> Selection { get; } = [];

    /// Puts the request behind a confirmation, unless the setting says otherwise — and always
    /// behind the running-game check, which the setting does not cover.
    private void Ask(string what, Func<IReadOnlyList<string>, ReconcileResult> work, bool needsSelection = true)
    {
        if (_game() is not { } game) { Status = "The game is not open."; return; }

        var mods = needsSelection ? Chosen : [];
        if (needsSelection && mods.Count == 0) { Status = "Nothing selected."; return; }

        var subject = mods.Count switch
        {
            0 => "everything installed",
            1 => $"'{mods[0].Name}'",
            _ => $"{mods.Count} mods",
        };

        var running = GameProcess.IsRunning(game);
        if (!running && !ConfirmChanges)
        {
            _ = Run(what, mods, work);
            return;
        }

        Asking = new Confirmation(
            $"{what} {subject}?",
            running
                ? "The game is running. Its bundles are open, and rewriting them now can leave a "
                  + "half-written file behind. Close it first."
                : (mods.Count > 1 ? string.Join(", ", mods.Select(m => m.Name)) + "\n\n" : "")
                  + "Every bundle these mods touch is restored from its backup and the enabled ones "
                  + "are applied again, so the result is the same however this was reached.",
            () => running ? Task.CompletedTask : Run(what, mods, work));
    }

    private async Task Run(
        string what, IReadOnlyList<InstalledMod> mods, Func<IReadOnlyList<string>, ReconcileResult> work)
    {
        Asking = null;
        Busy = true;

        var ids = mods.Select(m => m.Id).ToList();

        async Task Apply()
        {
            var result = await Task.Run(() => work(ids));
            Status = $"{what}: {result.Applied.Count} applied, {result.Restored.Count} restored"
                + (result.Failed.Count > 0
                    ? $", {result.Failed.Count} failed — {string.Join("; ", result.Failed)}"
                    : "");
        }

        try
        {
            if (Around is not null) await Around(Apply); else await Apply();
            Refresh();
        }
        catch (Exception ex)
        {
            Status = $"{ex.Message}  (while changing the game)";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => Asking = null;

    [RelayCommand]
    private async Task Proceed()
    {
        // Run owns the busy flag: it raises it before the work and lowers it in a finally. Raising
        // it again here — after the work had already finished and lowered it — left the manager
        // stuck showing itself as busy for the rest of the session, and every confirmed action
        // after the first one looked like it had hung.
        if (Asking is { } asking) await asking.Proceed();
        Asking = null;
    }

    [RelayCommand]
    private void CloseGame()
    {
        if (_game() is not { } game) return;

        Status = GameProcess.Close(game, TimeSpan.FromSeconds(10))
            ? "The game has been closed."
            : "The game did not close; end it yourself and try again.";
        Refresh();
    }

    [RelayCommand]
    private void LaunchGame()
    {
        if (_game() is not { } game) return;

        Status = GameProcess.Launch(game) ? "Starting the game…" : "The game could not be started.";
    }
}
