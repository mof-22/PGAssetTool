using System.Net;
using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// Fetching the content the game downloads as it is played.
///
/// Nothing here touches the network: the server is a handler that answers from a table, which is
/// what lets a test say what a 403, a truncated answer or a flaky connection does. That a fetched
/// bundle is byte for byte what the game would have downloaded was settled against the real CDN,
/// which no test can carry.
public class BundleFetcherTests
{
    /// The url in the catalog is not the url that serves the file: the name and the hash that the
    /// catalog joins with an underscore are two directories at the server, and the file inside is
    /// named after the bundle again. Asking for the catalog's own url is answered with 403.
    [Fact]
    public void TheCatalogsUrlIsNotTheUrlThatServesTheFile()
        => Assert.Equal(
            "https://cdn.test/aa/6000/windows/26.15/weaponskins_remote_x1_assets_all"
                + "/0123456789abcdef0123456789abcdef/weaponskins_remote_x1_assets_all",
            BundleFetcher.UrlOf(Remote()));

    [Fact]
    public void ABundleThatShipsWithTheInstallHasNowhereToBeFetchedFrom()
        => Assert.Throws<InvalidOperationException>(() => BundleFetcher.UrlOf(Local()));

    /// What a fetch has to do, and after an update what it has left to do, are the same question:
    /// the cache path holds the content hash, so a bundle an update did not change is already at
    /// its path and a changed one is not.
    [Fact]
    public void WhatIsMissingIsTheRemoteBundlesNotAtTheirPath()
    {
        using var scratch = new Scratch();
        var there = Remote(hash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var notThere = Remote(hash: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Put(scratch.Path, there, [1, 2, 3]);
        AddressablesCache.Register(there.PathUnder(scratch.Path));

        Assert.Equal(
            [notThere.Hash],
            BundleFetcher.Missing([there, notThere, Local()], scratch.Path).Select(b => b.Hash));
    }

    [Fact]
    public void AFetchedBundleLandsAtThePathTheGameLoadsItFrom()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 4);
        // Four bytes are not a bundle, and where they end up is what this is about; the check on
        // what came back has its own test.
        using var fetcher = Fetcher(
            new Server { [BundleFetcher.UrlOf(bundle)] = [1, 2, 3, 4] }, verify: false);

        var result = fetcher.Fetch([bundle], scratch.Path);

        Assert.Equal(1, result.Fetched);
        Assert.Empty(result.Failures);
        Assert.Equal<byte[]>([1, 2, 3, 4], File.ReadAllBytes(bundle.PathUnder(scratch.Path)));
    }

    /// Nothing is ever at the final path half-written, which is the whole of how resuming works: a
    /// file being there is taken as the bundle being there, so a download in progress must be
    /// somewhere else until it is whole.
    [Fact]
    public void AFetchThatFailsLeavesNothingBehind()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 4);
        using var fetcher = Fetcher(new Server { [BundleFetcher.UrlOf(bundle)] = [1, 2] });

        var result = fetcher.Fetch([bundle], scratch.Path);

        Assert.Equal(FetchState.Failed, Assert.Single(result.Outcomes).State);
        Assert.False(File.Exists(bundle.PathUnder(scratch.Path)));
        Assert.Empty(Directory.EnumerateFiles(scratch.Path, "*", SearchOption.AllDirectories));
    }

    /// A refusal is served as a short xml body with a 200-shaped size, so a run that had the url
    /// wrong would fill the cache with error pages if the answer were taken on trust.
    [Fact]
    public void WhatTheServerRefusesIsNotKept()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 4);
        using var fetcher = Fetcher(new Server
        {
            Refusals = { [BundleFetcher.UrlOf(bundle)] = HttpStatusCode.Forbidden },
        });

        var outcome = Assert.Single(fetcher.Fetch([bundle], scratch.Path).Outcomes);

        Assert.Equal(FetchState.Failed, outcome.State);
        Assert.Contains("403", outcome.Trouble);
        Assert.False(File.Exists(bundle.PathUnder(scratch.Path)));
    }

    /// The size is what the catalog says and the CRC is what the game checks the contents against.
    /// Bytes of the right length that are not the bundle get past the first and not the second.
    [Fact]
    public void BytesOfTheRightLengthThatAreNotTheBundleAreNotKept()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 64);
        var server = new Server { [BundleFetcher.UrlOf(bundle)] = new byte[64] };

        using (var checking = Fetcher(server))
        {
            var outcome = Assert.Single(checking.Fetch([bundle], scratch.Path).Outcomes);
            Assert.Equal(FetchState.Failed, outcome.State);
            Assert.Contains("hashes to", outcome.Trouble);
            Assert.False(File.Exists(bundle.PathUnder(scratch.Path)));
        }

        // And the check is what rejected it, rather than something else about those bytes.
        using var trusting = Fetcher(server, verify: false);
        Assert.Equal(1, trusting.Fetch([bundle], scratch.Path).Fetched);
        Assert.True(File.Exists(bundle.PathUnder(scratch.Path)));
    }

    /// A directory holding only the bytes is not a cache hit. Unity reads `__info` beside them and
    /// downloads the bundle again without one, so 12,523 bundles fetched into the game's own cache
    /// sat there and the game fetched every one of them a second time.
    [Fact]
    public void AFetchedBundleIsRegisteredWithTheGame()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 4);
        using var fetcher = Fetcher(
            new Server { [BundleFetcher.UrlOf(bundle)] = [1, 2, 3, 4] }, verify: false);

        fetcher.Fetch([bundle], scratch.Path);

        var info = Path.Combine(
            Path.GetDirectoryName(bundle.PathUnder(scratch.Path))!, AddressablesCache.InfoFileName);
        Assert.True(File.Exists(info));

        var lines = File.ReadAllText(info).Split('\n');
        Assert.Equal("-1", lines[0]);
        Assert.Equal("1", lines[2]);
        Assert.Equal(AddressablesCache.DataFileName, lines[3]);

        // The second line is when the bytes were written, which is what the game puts there.
        var written = new DateTimeOffset(
            File.GetLastWriteTimeUtc(bundle.PathUnder(scratch.Path)), TimeSpan.Zero);
        Assert.Equal(written.ToUnixTimeSeconds(), long.Parse(lines[1]));
    }

    /// Bytes already on disk with no note beside them are put right without downloading, which is
    /// the difference between twenty-three bytes and four and a half gigabytes.
    [Fact]
    public void BytesAlreadyThereAreRegisteredWithoutDownloadingThemAgain()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 3);
        Put(scratch.Path, bundle, [1, 2, 3]);
        var server = new Server();
        using var fetcher = Fetcher(server, verify: false);

        var plan = BundleFetcher.Plan([bundle], scratch.Path);
        Assert.Empty(plan.ToDownload);
        Assert.Equal([bundle], plan.ToRegister);
        Assert.Equal(0, plan.Bytes);

        Assert.Equal(1, fetcher.Fetch(plan.All, scratch.Path).AlreadyThere);
        Assert.Empty(server.Asked);
        Assert.True(AddressablesCache.IsCached(bundle.PathUnder(scratch.Path)));

        // And once it is right it is not a job any more.
        Assert.Empty(BundleFetcher.Missing([bundle], scratch.Path));
    }

    [Fact]
    public void ABundleAlreadyAtItsPathIsNotAskedFor()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 3);
        Put(scratch.Path, bundle, [1, 2, 3]);
        AddressablesCache.Register(bundle.PathUnder(scratch.Path));
        var server = new Server();
        using var fetcher = Fetcher(server);

        var result = fetcher.Fetch([bundle], scratch.Path);

        Assert.Equal(1, result.AlreadyThere);
        Assert.Empty(server.Asked);
    }

    /// One bundle's connection dropping is not the fetch failing. Over twelve thousand of them, a
    /// single attempt each would end most runs short of the end.
    [Fact]
    public void AConnectionThatDropsIsTriedAgain()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 4);
        var server = new Server { [BundleFetcher.UrlOf(bundle)] = [1, 2, 3, 4] };
        server.DropFirst[BundleFetcher.UrlOf(bundle)] = 2;
        using var fetcher = Fetcher(server, verify: false);

        Assert.Equal(1, fetcher.Fetch([bundle], scratch.Path).Fetched);
        Assert.Equal(3, server.Asked.Count);
    }

    [Fact]
    public void AndIsGivenUpOnEventually()
    {
        using var scratch = new Scratch();
        var bundle = Remote(size: 4);
        var server = new Server { [BundleFetcher.UrlOf(bundle)] = [1, 2, 3, 4] };
        server.DropFirst[BundleFetcher.UrlOf(bundle)] = 99;
        using var fetcher = Fetcher(server, attempts: 2, verify: false);

        Assert.Single(fetcher.Fetch([bundle], scratch.Path).Failures);
        Assert.Equal(2, server.Asked.Count);
    }

    [Fact]
    public void ABundleThatShipsWithTheInstallIsPassedOver()
    {
        using var scratch = new Scratch();
        using var fetcher = Fetcher(new Server());

        var outcome = Assert.Single(fetcher.Fetch([Local()], scratch.Path).Outcomes);

        Assert.Equal(FetchState.NotFetchable, outcome.State);
    }

    /// Stopping is the one thing a four-gigabyte download has to do well. What it already fetched
    /// stays fetched, and the run says it was stopped rather than reporting the rest as failures.
    [Fact]
    public void StoppingKeepsWhatWasAlreadyFetched()
    {
        using var scratch = new Scratch();
        var bundles = Enumerable.Range(0, 40).Select(i => Remote(hash: Hash(i), size: 4)).ToArray();
        var server = new Server();
        foreach (var b in bundles) server[BundleFetcher.UrlOf(b)] = [1, 2, 3, 4];

        using var stop = new CancellationTokenSource();
        using var fetcher = Fetcher(server, atOnce: 1, verify: false);

        var result = fetcher.Fetch(bundles, scratch.Path,
            p => { if (p.Done >= 5) stop.Cancel(); }, stop.Token);

        Assert.True(result.Stopped);
        Assert.InRange(result.Fetched, 1, bundles.Length - 1);
        Assert.Equal(
            result.Fetched,
            Directory.EnumerateFiles(scratch.Path, "__data", SearchOption.AllDirectories).Count());
    }

    /// Progress is what a long run is watched by, so it has to count every bundle exactly once and
    /// end on the last one.
    [Fact]
    public void EveryBundleIsReportedOnceAndTheLastOneIsTheLast()
    {
        using var scratch = new Scratch();
        var bundles = Enumerable.Range(0, 12).Select(i => Remote(hash: Hash(i), size: 4)).ToArray();
        var server = new Server();
        foreach (var b in bundles) server[BundleFetcher.UrlOf(b)] = [1, 2, 3, 4];
        using var fetcher = Fetcher(server, verify: false);

        var seen = new List<FetchProgress>();
        fetcher.Fetch(bundles, scratch.Path, p => { lock (seen) seen.Add(p); });

        Assert.Equal(bundles.Length, seen.Count);
        Assert.Equal(Enumerable.Range(1, bundles.Length), seen.Select(p => p.Done).Order());
        Assert.Equal(bundles.Sum(b => b.Size), seen.Max(p => p.Bytes));
        Assert.All(seen, p => Assert.Equal(bundles.Length, p.Total));
    }

    /// Hashing what is on disk tells three things apart: the bundle, nothing, and something else of
    /// the same length. Only the third needs the hash, and it is the one that matters — a bundle
    /// something has written into is at its path and the right size.
    [Fact]
    public void WhatIsOnDiskIsToldApartFromWhatShouldBe()
    {
        using var scratch = new Scratch();
        var here = Remote(hash: Hash(1), size: 3);
        var absent = Remote(hash: Hash(2), size: 3);
        var wrong = Remote(hash: Hash(3), size: 64);

        // Neither of these is a bundle, so neither hashes to the catalog's number; what separates
        // them is that one is not there at all.
        Put(scratch.Path, here, [1, 2, 3]);
        Put(scratch.Path, wrong, new byte[64]);

        using var fetcher = Fetcher(new Server());
        var states = fetcher.Check([here, absent, wrong, Local()], scratch.Path);

        Assert.Equal(3, states.Count);
        Assert.Equal(BundleFetcher.BundleState.Wrong, states[here]);
        Assert.Equal(BundleFetcher.BundleState.Absent, states[absent]);
        Assert.Equal(BundleFetcher.BundleState.Wrong, states[wrong]);
    }

    /// Every bundle is reported once, which is what a count printed beside it is read from.
    [Fact]
    public void CheckingCountsEveryBundleOnce()
    {
        using var scratch = new Scratch();
        var bundles = Enumerable.Range(0, 9).Select(i => Remote(hash: Hash(i), size: 4)).ToArray();

        using var fetcher = Fetcher(new Server());
        var seen = new List<int>();
        fetcher.Check(bundles, scratch.Path, (done, total) =>
        {
            lock (seen) seen.Add(done);
            Assert.Equal(bundles.Length, total);
        });

        Assert.Equal(Enumerable.Range(1, bundles.Length), seen.Order());
    }

    private const string TheHash = "0123456789abcdef0123456789abcdef";

    private static string Hash(int i) => i.ToString("x2") + TheHash[2..];

    private static CatalogBundle Remote(string hash = TheHash, long size = 1)
        => new(
            FileName: $"weaponskins_remote_x1_assets_all_{hash}.bundle",
            CacheName: "c" + hash,
            Hash: hash,
            Crc: 1234,
            Size: size,
            IsRemote: true,
            InternalId: "https://cdn.test/aa/6000/windows/26.15/"
                + $"weaponskins_remote_x1_assets_all_{hash}.bundle");

    private static CatalogBundle Local()
        => new(
            FileName: $"shipped_assets_all_{TheHash}.bundle",
            CacheName: "shipped",
            Hash: TheHash,
            Crc: 1234,
            Size: 1,
            IsRemote: false,
            InternalId: @"{UnityEngine.AddressableAssets.Addressables.RuntimePath}"
                + $@"\StandaloneWindows64\shipped_assets_all_{TheHash}.bundle");

    private static void Put(string cacheRoot, CatalogBundle bundle, byte[] contents)
    {
        var path = bundle.PathUnder(cacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static BundleFetcher Fetcher(
        Server server, int atOnce = 4, int attempts = 3, bool verify = true)
        => new(new HttpClient(server)) { AtOnce = atOnce, Attempts = attempts, Verify = verify };

    private sealed class Server : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public Dictionary<string, HttpStatusCode> Refusals { get; } = [];

        /// How many times each url drops the connection before it starts answering.
        public Dictionary<string, int> DropFirst { get; } = [];

        public List<string> Asked { get; } = [];

        public byte[] this[string url] { set => _files[url] = value; }

        public void Add(string url, byte[] contents) => _files[url] = contents;

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Asked) Asked.Add(url);

            lock (DropFirst)
                if (DropFirst.TryGetValue(url, out var left) && left > 0)
                {
                    DropFirst[url] = left - 1;
                    throw new HttpRequestException("the connection was closed");
                }

            if (Refusals.TryGetValue(url, out var refused))
                return new HttpResponseMessage(refused) { ReasonPhrase = refused.ToString() };

            return _files.TryGetValue(url, out var contents)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"pgat-fetch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
