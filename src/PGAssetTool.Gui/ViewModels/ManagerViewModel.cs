using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Gui.ViewModels;

/// An installed mod as a row.
public sealed record InstalledRow(InstalledMod Mod)
{
    public string Name => Mod.Name.Length > 0 ? Mod.Name : Mod.Id;
    public bool Enabled => Mod.Enabled;

    public string Detail =>
        $"{Mod.Id}   {Mod.Version}   {Mod.TouchedBundles.Count} bundle(s)"
        + $"   installed {Mod.InstalledAt:yyyy-MM-dd} for {Mod.GameVersion}";
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
    private void Enable() => Ask("Turn on", m => new ModApplier(_game()!, new ModStore(_game()!)).SetEnabled(m.Id, true));

    [RelayCommand]
    private void Disable() => Ask("Turn off", m => new ModApplier(_game()!, new ModStore(_game()!)).SetEnabled(m.Id, false));

    [RelayCommand]
    private void Remove() => Ask("Remove", m => new ModApplier(_game()!, new ModStore(_game()!)).Remove(m.Id));

    [RelayCommand]
    private void Reapply() => Ask("Reapply everything",
        _ => new ModApplier(_game()!, new ModStore(_game()!)).Reconcile());

    /// Puts the request behind a confirmation, unless the setting says otherwise — and always
    /// behind the running-game check, which the setting does not cover.
    private void Ask(string what, Func<InstalledMod, ReconcileResult> work)
    {
        if (_game() is not { } game) { Status = "The game is not open."; return; }
        if (Selected is not { } row && what != "Reapply everything") { Status = "Nothing selected."; return; }

        var mod = Selected?.Mod;
        var subject = mod is null ? "everything installed" : $"'{mod.Name}'";

        var running = GameProcess.IsRunning(game);
        if (!running && !ConfirmChanges)
        {
            _ = Run(what, mod, work);
            return;
        }

        Asking = new Confirmation(
            $"{what} {subject}?",
            running
                ? "The game is running. Its bundles are open, and rewriting them now can leave a "
                  + "half-written file behind. Close it first."
                : "Every bundle these mods touch is restored from its backup and the enabled ones "
                  + "are applied again, so the result is the same however this was reached.",
            () => running ? Task.CompletedTask : Run(what, mod, work));
    }

    private async Task Run(string what, InstalledMod? mod, Func<InstalledMod, ReconcileResult> work)
    {
        Asking = null;
        Busy = true;

        var subject = mod ?? new InstalledMod
        {
            Id = "", Name = "", PackPath = "", InstalledAt = DateTimeOffset.Now,
            GameVersion = "", TouchedBundles = new Dictionary<string, string>(),
        };

        async Task Apply()
        {
            var result = await Task.Run(() => work(subject));
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
