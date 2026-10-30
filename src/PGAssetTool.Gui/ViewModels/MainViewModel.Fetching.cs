using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PGAssetTool.Core.Addressables;
using PGAssetTool.Core.Game;
using PGAssetTool.Core.Settings;

namespace PGAssetTool.Gui.ViewModels;

/// Downloading the content the game only fetches as it is played.
///
/// Since 26.15 almost nothing an item is made of ships with the installation: the game downloads a
/// weapon or a map when the player picks one, and offers no way to ask for the rest. So on a fresh
/// installation most of the catalog is not on disk, and nothing here can read what is not there.
public sealed partial class MainViewModel
{
    /// The catalog and the cache, opened beside the bundle reader. Null on an installation from
    /// before the move, where there is no catalog to read.
    private AddressableBundles? _addressables;

    private CancellationTokenSource? _stopFetch;

    [ObservableProperty] private bool _fetching;

    partial void OnFetchingChanged(bool value) => WorkingChanged();

    [ObservableProperty] private int _fetchDone;
    [ObservableProperty] private int _fetchTotal;
    [ObservableProperty] private long _fetchBytes;
    [ObservableProperty] private long _fetchTotalBytes;

    /// How many bundles the fetch downloads at once. See BundleFetcher.AtOnce.
    [ObservableProperty] private int _fetchAtOnce = 8;

    partial void OnFetchAtOnceChanged(int value) => Remember();

    /// Where the fetch writes. Empty is the cache the game loads from, which is the answer that
    /// makes what is fetched usable; anywhere else is a copy to look at.
    [ObservableProperty] private string _fetchDirectory = "";

    partial void OnFetchDirectoryChanged(string value)
    {
        Remember();
        OnPropertyChanged(nameof(FetchDescribed));
    }

    public IReadOnlyList<int> FetchParallelism { get; } = [1, 2, 4, 6, 8, 12, 16, 24, 32];

    public string FetchDescribed => FetchDirectory.Trim().Length == 0
        ? _addressables?.Cache is { } cache
            ? $"The game's own: {cache.Root}"
            : "The game's own, which is not there until the game has been started once."
        : "A copy. The game loads none of it from here.";

    /// Bound to the bar in the status line while a fetch is running.
    public double FetchFraction => FetchTotal == 0 ? 0 : (double)FetchDone / FetchTotal;

    public string FetchProgress => FetchTotal == 0
        ? ""
        : $"{FetchDone:N0} / {FetchTotal:N0}  ·  {Megabytes(FetchBytes)} of {Megabytes(FetchTotalBytes)}";

    partial void OnFetchDoneChanged(int value)
    {
        OnPropertyChanged(nameof(FetchFraction));
        OnPropertyChanged(nameof(FetchProgress));
    }

    partial void OnFetchBytesChanged(long value) => OnPropertyChanged(nameof(FetchProgress));

    /// Said in whatever unit the number is legible in: four and a half gigabytes and a single
    /// three-kilobyte bundle both come up here.
    private static string Megabytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:N2} GB",
    };

    /// Where a fetch would write, or null when there is nowhere: an installation from before the
    /// move, or one the game has never been started on and no directory named by hand.
    private string? FetchInto()
    {
        if (FetchDirectory.Trim() is { Length: > 0 } named) return Path.GetFullPath(named);
        return _addressables?.Cache?.Root;
    }

    /// What a fetch has left to do. The cache path holds the bundle's content hash, so this is the
    /// same question before a first fetch, after one that was stopped, and after a game update —
    /// a bundle an update did not change is already at its path and a changed one is not.
    private BundleFetcher.FetchPlan Outstanding()
        => _addressables is not { } bundles || FetchInto() is not { } into
            ? new BundleFetcher.FetchPlan([], [])
            : BundleFetcher.Plan(bundles.Catalog.Bundles, into);

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchEverything()
    {
        if (_addressables is null || FetchInto() is not { } into) return;

        var plan = Outstanding();
        var wanted = plan.All;
        if (wanted.Count == 0)
        {
            Status = "Every bundle the catalog names is already here.";
            ContentNotice = null;
            return;
        }

        // Writing into the cache the game has open is the same hazard as rewriting its bundles, and
        // it is the manager's answer to it as well. A directory named by hand is nobody's but ours.
        if (FetchDirectory.Trim().Length == 0 && _installation is { } game && GameProcess.IsRunning(game))
        {
            Status = "The game is running, and writing into the cache it has open now can leave a "
                + "half-written bundle behind. Close it, then try again.";
            return;
        }

        await RunFetch(wanted, into, plan.Bytes);
    }

    private bool CanFetch() => !Fetching && _addressables is not null && FetchInto() is not null;

    private async Task RunFetch(IReadOnlyList<CatalogBundle> wanted, string into, long bytes)
    {
        Fetching = true;
        FetchEverythingCommand.NotifyCanExecuteChanged();
        FetchDone = 0;
        FetchTotal = wanted.Count;
        FetchBytes = 0;
        FetchTotalBytes = bytes;
        ContentNotice = null;
        Status = $"Downloading {wanted.Count:N0} bundles ({Megabytes(FetchTotalBytes)})…";

        using var stop = new CancellationTokenSource();
        _stopFetch = stop;
        try
        {
            // The progress comes off whichever of the eight threads finished a bundle, and the
            // properties it sets are bound; the window owns them. Reported on a timer rather than
            // per bundle, because twelve thousand of them is twelve thousand layout passes.
            var done = 0;
            var sofar = 0L;
            var result = await Task.Run(() =>
            {
                using var fetcher = new BundleFetcher { AtOnce = Math.Max(1, FetchAtOnce) };
                return fetcher.Fetch(wanted, into, p =>
                {
                    Interlocked.Exchange(ref done, p.Done);
                    Interlocked.Exchange(ref sofar, p.Bytes);
                }, stop.Token);
            }, stop.Token).WithTicks(() => { FetchDone = done; FetchBytes = sofar; });

            FetchDone = result.Fetched + result.AlreadyThere + result.Failures.Count;

            Status = result.Stopped
                ? $"Stopped. {result.Fetched:N0} bundles came down; what is here stays here and "
                    + "starting again carries on from it."
                : result.Failures.Count == 0
                    ? $"{result.Fetched:N0} bundles downloaded."
                    : $"{result.Fetched:N0} downloaded, {result.Failures.Count:N0} failed — "
                        + $"{result.Failures[0].Trouble}. Starting again retries them.";

            _fetchedSomething = result.Fetched > 0;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. What is here stays here and starting again carries on from it.";
        }
        catch (Exception ex)
        {
            Status = Describe(ex, "downloading the game's content");
        }
        finally
        {
            _stopFetch = null;
            Fetching = false;
            FetchEverythingCommand.NotifyCanExecuteChanged();
        }

        // After the fetch rather than inside it, so the bar and the stop button are gone while the
        // game is read again. Whatever came down is not in the reader, which read the installation
        // as it was when it opened.
        if (_fetchedSomething)
        {
            _fetchedSomething = false;
            var said = Status;
            await ReloadAsync();
            Status = said;
        }
    }

    private bool _fetchedSomething;

    [RelayCommand]
    private void StopFetch() => _stopFetch?.Cancel();

    /// Said across the top when there is content to fetch. Separate from the mod notice below it
    /// in the window, and worked out at the same moment.
    [ObservableProperty] private string? _contentNotice;

    [RelayCommand]
    private void DismissContent() => ContentNotice = null;

    /// Worked out on opening the game: what is missing is a property of the installation, and
    /// nothing else about the tool looks wrong when most of the catalog is not on disk.
    private async Task NoticeWhatIsMissing()
    {
        OnPropertyChanged(nameof(FetchDescribed));
        FetchEverythingCommand.NotifyCanExecuteChanged();

        // Twelve thousand of these, each one a pair of files that are or are not there: off the
        // window's thread, because it is the window coming up that is waiting for it.
        var plan = await Task.Run(Outstanding);
        if (plan.All.Count == 0) { ContentNotice = null; return; }

        ContentNotice = plan.ToDownload.Count > 0
            ? $"{plan.ToDownload.Count:N0} of the game's bundles ({Megabytes(plan.Bytes)}) have not "
                + "been downloaded."
                // Why it matters is worth a sentence while most of the game is missing and reads as
                // pointless once a handful are, which is where a finished fetch leaves it.
                + (plan.ToDownload.Count > 100
                    ? " The game fetches one when a weapon or a map is picked, so until they are "
                        + "here most of what it has cannot be read."
                    : " Whatever is in them cannot be read until they are.")
            // Nothing to download and something to put right is its own sentence: there is no wait
            // and no size, and what it buys is the game seeing what is already on disk.
            : $"{plan.ToRegister.Count:N0} bundles are on disk but the game cannot see them, so it "
                + "would download them again. Putting that right takes no time and no network.";

        if (plan.ToDownload.Count > 0 && plan.ToRegister.Count > 0)
            ContentNotice += $" Another {plan.ToRegister.Count:N0} are here already but the game "
                + "cannot see them; those are put right at the same time.";

        // The order matters when both notices are up: the content comes down first and the mods go
        // back on over it.
        if (UpdateNotice is not null)
            ContentNotice += " Download these before reapplying the mods below.";
    }

    private void ReadFetchSettings(ToolSettings settings)
    {
        FetchAtOnce = settings.FetchAtOnce;
        FetchDirectory = settings.FetchDirectory;
    }
}

/// Runs a ticker alongside a task, so work on a thread of its own can report through properties the
/// window is bound to without posting to it once per item.
internal static class TaskTicks
{
    public static async Task<T> WithTicks<T>(this Task<T> work, Action tick)
    {
        while (await Task.WhenAny(work, Task.Delay(100)) != work) tick();
        tick();
        return await work;
    }
}
