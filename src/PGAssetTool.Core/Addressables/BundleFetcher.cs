using System.Net;

namespace PGAssetTool.Core.Addressables;

/// What became of one bundle.
public enum FetchState
{
    /// Downloaded and, where asked for, checked against the catalog's CRC.
    Fetched,

    /// Already at the path the catalog gives, so nothing was asked of the network.
    AlreadyThere,

    /// A bundle that ships with the install. There is nowhere to fetch it from.
    NotFetchable,

    /// Every attempt failed, or what came back was not what the catalog describes.
    Failed,
}

public sealed record FetchOutcome(CatalogBundle Bundle, FetchState State, string? Trouble = null);

/// How far along a fetch is. <paramref name="Bytes"/> counts only what was downloaded, so a run that
/// finds everything already there ends at zero however many bundles it went through.
public sealed record FetchProgress(
    int Done, int Total, long Bytes, long TotalBytes, string Name, FetchState State);

public sealed record FetchResult(IReadOnlyList<FetchOutcome> Outcomes, bool Stopped)
{
    public int Fetched => Outcomes.Count(o => o.State is FetchState.Fetched);
    public int AlreadyThere => Outcomes.Count(o => o.State is FetchState.AlreadyThere);
    public IReadOnlyList<FetchOutcome> Failures
        => [.. Outcomes.Where(o => o.State is FetchState.Failed)];
}

/// Fetches the bundles the catalog describes into the cache the game loads them from.
///
/// The game downloads a weapon or a map the first time the player picks one and offers no way to
/// ask for the rest, which leaves most of the content unreachable to anything reading the
/// installation. What is fetched here is the same bytes at the same path: a bundle written by this
/// and a bundle written by the game are indistinguishable, which was measured byte for byte.
///
/// **The url in the catalog is not the url that serves the file.** The catalog's internal id ends
/// `&lt;name&gt;_&lt;hash&gt;.bundle`, and asking for that is answered with 403; the file is at
/// `&lt;name&gt;/&lt;hash&gt;/&lt;name&gt;` under the same root. A 403 body is 111 bytes of xml, so a
/// run that got the transform wrong would quietly fill the cache with error pages — which is why
/// what comes back is checked against the size and the CRC the catalog gives before it is kept.
public sealed class BundleFetcher : IDisposable
{
    public const int DefaultAtOnce = 8;

    /// The name a download goes under until it is whole. Nothing reads it, and the final path is
    /// only ever a complete file — which is what lets a resume trust that a file being there means
    /// the bundle is there.
    private const string Unfinished = "__data.fetching";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public BundleFetcher(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    /// How many bundles are in flight at once.
    ///
    /// Two different limits meet here. The large bundles saturate the line well before six: the
    /// forty largest, 584MB of them, took 31s at six, 29s at sixteen and 30s at thirty-two — about
    /// 19MB/s whichever. The small ones are latency and nothing else: 150 of the smallest, 235KB
    /// in all, took 1.6s at eight, 1.1s at sixteen and 0.8s at thirty-two. Eight is where the
    /// second curve has given up most of what it has while the first is already at the line's
    /// speed, and the whole catalog is big bundles by weight.
    public int AtOnce { get; init; } = DefaultAtOnce;

    /// How many times one bundle is asked for before it is given up on.
    public int Attempts { get; init; } = 3;

    /// Whether what came back is hashed before it is kept. Off, only the size is checked, which
    /// costs nothing but accepts a file of the right length that is not the bundle.
    public bool Verify { get; init; } = true;

    /// Whether a bundle already at its path is hashed rather than taken as correct. Off — which is
    /// the default — a file being there is enough, and that is what makes resuming cheap: a run
    /// over the whole catalog with everything present touches nothing but the directory listing.
    public bool VerifyWhatIsThere { get; init; }

    /// What a fetch has left to do, which is two different jobs.
    ///
    /// <paramref name="ToDownload"/> is what is not here at all — both what a first fetch has to do
    /// and what an update left to do, since the cache path holds the content hash and so a bundle an
    /// update did not change is already at its path.
    ///
    /// <paramref name="ToRegister"/> is the bundles whose bytes are here with no `__info` beside
    /// them. Those cost nothing to put right and the game cannot see them until they are.
    public sealed record FetchPlan(
        IReadOnlyList<CatalogBundle> ToDownload, IReadOnlyList<CatalogBundle> ToRegister)
    {
        public IReadOnlyList<CatalogBundle> All => [.. ToDownload, .. ToRegister];

        /// What has to come down the wire, which is not the same as how many bundles are to be
        /// dealt with.
        public long Bytes => ToDownload.Sum(b => b.Size);
    }

    public static FetchPlan Plan(IEnumerable<CatalogBundle> bundles, string cacheRoot)
    {
        var download = new List<CatalogBundle>();
        var register = new List<CatalogBundle>();
        foreach (var bundle in bundles)
        {
            if (!bundle.IsRemote) continue;
            var path = bundle.PathUnder(cacheRoot);
            if (!File.Exists(path)) download.Add(bundle);
            else if (!AddressablesCache.IsCached(path)) register.Add(bundle);
        }
        return new FetchPlan(download, register);
    }

    /// The bundles that are not in the cache as the game keeps one, by either measure.
    public static IReadOnlyList<CatalogBundle> Missing(
        IEnumerable<CatalogBundle> bundles, string cacheRoot)
        => Plan(bundles, cacheRoot).All;

    /// What a bundle on disk is: absent, or present with bytes that do not hash to the number the
    /// catalog records for it.
    public enum BundleState { Here, Absent, Wrong }

    /// The state of each of the given bundles, hashing every one that is there.
    ///
    /// Slow — it decompresses everything it checks — and the only way to answer whether the content
    /// on disk is the content the game expects. A fetch does not need this: a file at the catalog's
    /// path got there by being checked, and re-hashing four and a half gigabytes to find that out
    /// again is what `VerifyWhatIsThere` is off by default for.
    public IReadOnlyDictionary<CatalogBundle, BundleState> Check(
        IEnumerable<CatalogBundle> bundles, string cacheRoot,
        Action<int, int>? progress = null, CancellationToken cancel = default)
    {
        var wanted = bundles.Where(b => b.IsRemote).ToArray();
        var states = new BundleState[wanted.Length];
        var done = 0;

        Parallel.For(0, wanted.Length,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancel },
            at =>
            {
                var path = wanted[at].PathUnder(cacheRoot);
                states[at] = !File.Exists(path)
                    ? BundleState.Absent
                    : Crc(path) == wanted[at].Crc ? BundleState.Here : BundleState.Wrong;

                progress?.Invoke(Interlocked.Increment(ref done), wanted.Length);
            });

        var answer = new Dictionary<CatalogBundle, BundleState>(wanted.Length);
        for (var at = 0; at < wanted.Length; at++) answer[wanted[at]] = states[at];
        return answer;
    }

    /// Where this bundle is actually served from.
    public static string UrlOf(CatalogBundle bundle)
    {
        if (!bundle.IsRemote)
            throw new InvalidOperationException(
                $"'{bundle.FileName}' ships with the installation and is not downloaded.");

        var at = bundle.InternalId.LastIndexOf('/');
        var root = at < 0 ? "" : bundle.InternalId[..at];
        var name = bundle.FileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
            ? bundle.FileName[..^".bundle".Length]
            : bundle.FileName;
        if (name.EndsWith("_" + bundle.Hash, StringComparison.OrdinalIgnoreCase))
            name = name[..^(bundle.Hash.Length + 1)];

        return $"{root}/{name}/{bundle.Hash}/{name}";
    }

    /// Fetches what is missing of <paramref name="wanted"/> into <paramref name="cacheRoot"/>.
    ///
    /// Stopping is safe at any point: a bundle is either wholly at its path or not there at all, so
    /// the next run carries on by the same rule it started with.
    public FetchResult Fetch(
        IEnumerable<CatalogBundle> wanted,
        string cacheRoot,
        Action<FetchProgress>? progress = null,
        CancellationToken cancel = default)
    {
        var bundles = wanted.ToArray();
        var outcomes = new FetchOutcome?[bundles.Length];

        var totalBytes = bundles.Where(b => b.IsRemote).Sum(b => b.Size);
        var done = 0;
        var bytes = 0L;
        var told = new object();

        try
        {
            Parallel.For(0, bundles.Length,
                new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = cancel },
                at =>
                {
                    var outcome = One(bundles[at], cacheRoot, cancel);
                    outcomes[at] = outcome;

                    lock (told)
                    {
                        done++;
                        if (outcome.State is FetchState.Fetched) bytes += outcome.Bundle.Size;
                        progress?.Invoke(new FetchProgress(
                            done, bundles.Length, bytes, totalBytes,
                            outcome.Bundle.FileName, outcome.State));
                    }
                });
        }
        catch (OperationCanceledException)
        {
            return new FetchResult([.. outcomes.OfType<FetchOutcome>()], Stopped: true);
        }

        return new FetchResult([.. outcomes.OfType<FetchOutcome>()], Stopped: false);
    }

    private FetchOutcome One(CatalogBundle bundle, string cacheRoot, CancellationToken cancel)
    {
        if (!bundle.IsRemote) return new FetchOutcome(bundle, FetchState.NotFetchable);

        var path = bundle.PathUnder(cacheRoot);
        if (File.Exists(path) && IsWhole(bundle, path))
        {
            // The bytes are here; the note the game reads them by may not be, and writing it costs
            // twenty-three bytes against fetching the bundle again.
            AddressablesCache.Register(path);
            return new FetchOutcome(bundle, FetchState.AlreadyThere);
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var writing = Path.Combine(directory, Unfinished);

        string? trouble = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            trouble = Download(bundle, writing, cancel);
            if (trouble is null)
            {
                File.Move(writing, path, overwrite: true);
                AddressablesCache.Register(path);
                return new FetchOutcome(bundle, FetchState.Fetched);
            }

            Delete(writing);
            if (attempt < Attempts) cancel.WaitHandle.WaitOne(250 * attempt);
        }

        return new FetchOutcome(bundle, FetchState.Failed, trouble);
    }

    /// Null when the file now at <paramref name="writing"/> is this bundle, and otherwise what was
    /// wrong with it.
    private string? Download(CatalogBundle bundle, string writing, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UrlOf(bundle));
            using var response = _http.Send(
                request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                return $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}";

            using (var from = response.Content.ReadAsStream(cancel))
            using (var to = new FileStream(
                       writing, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                from.CopyTo(to);
                to.Flush(flushToDisk: true);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException
                                      && !cancel.IsCancellationRequested)
        {
            return e.Message;
        }

        var length = new FileInfo(writing).Length;
        if (length != bundle.Size)
            return $"{length:N0} bytes came back where the catalog says {bundle.Size:N0}";

        if (!Verify) return null;
        var crc = Crc(writing);
        return crc == bundle.Crc
            ? null
            : $"what came back hashes to {crc?.ToString() ?? "nothing readable"} "
                + $"where the catalog says {bundle.Crc}";
    }

    private bool IsWhole(CatalogBundle bundle, string path)
    {
        if (new FileInfo(path).Length != bundle.Size) return false;
        return !VerifyWhatIsThere || Crc(path) == bundle.Crc;
    }

    private static uint? Crc(string path)
    {
        // Anything at all from the bundle reader means these are not bundle bytes, which is the
        // same answer as a bundle that would not decompress: no CRC, so it is not what was asked
        // for. The library throws whatever the first unreadable field happens to raise.
        try { return BundleCrc.OfBundle(path); }
        catch (Exception) { return null; }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
