using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Tests;

/// The rules here were established by experiment, not documentation: the same icon was written red
/// into the shipped copy and green into the downloaded one. The game showed green; deleting the
/// downloaded file made it log "Failed to read data for the AssetBundle" and show red.
public class BundleResolutionTests : IDisposable
{
    private const string Hash = "1773b9185aa0e4998178684a254d5087";

    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-resolve").FullName;
    private readonly string _shipped;
    private readonly string _downloaded;

    public BundleResolutionTests()
    {
        var data = Path.Combine(_root, "Game_Data");
        _shipped = Path.Combine(data, "StreamingAssets", "Cache", "bundles");
        Directory.CreateDirectory(_shipped);
        File.WriteAllText(Path.Combine(_shipped, "embedded_asset_bundles.json"), "[]");
        File.WriteAllLines(Path.Combine(data, "app.info"), ["Test Company", "Test Product"]);
        _downloaded = Path.Combine(_root, "LocalLow", "Cache");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteShipped(string bundle, string hash, string content)
        => Write(Path.Combine(_shipped, bundle, hash, bundle), content);

    private void WriteDownloaded(string bundle, string hash, string content)
        => Write(Path.Combine(_downloaded, "bundles", bundle, hash, bundle), content);

    private void Claim(params string[] lines)
    {
        var names = Path.Combine(_downloaded, "Info", "names");
        Directory.CreateDirectory(Path.GetDirectoryName(names)!);
        File.WriteAllLines(names, lines);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ResolvedBundle? Resolve(string bundle)
    {
        var game = GameInstallation.Open(_root);
        var downloaded = Directory.Exists(_downloaded) ? DownloadedBundleCache.Open(_downloaded) : null;
        return Rebuild(game, downloaded).Resolve(bundle, Hash);
    }

    // GameInstallation finds the downloaded cache through the real profile path, which a test cannot
    // write to, so the cache under test is substituted here.
    private static GameInstallation Rebuild(GameInstallation game, DownloadedBundleCache? downloaded)
        => GameInstallation.OpenWith(game.RootDirectory, downloaded);

    [Fact]
    public void TheShippedCopyIsUsedWhenNothingHasBeenDownloaded()
    {
        WriteShipped("woi_0", Hash, "red");
        Assert.Equal(CacheKind.Shipped, Resolve("woi_0")!.Cache);
    }

    [Fact]
    public void TheDownloadedCopyWinsWhenTheLedgerClaimsIt()
    {
        WriteShipped("woi_0", Hash, "red");
        WriteDownloaded("woi_0", Hash, "green");
        Claim($"woi_0/{Hash}");

        var resolved = Resolve("woi_0")!;
        Assert.Equal(CacheKind.Downloaded, resolved.Cache);
        Assert.Equal("green", File.ReadAllText(resolved.Path));
    }

    [Fact]
    public void AFileInTheDownloadedCacheIsIgnoredUntilTheLedgerClaimsIt()
    {
        WriteShipped("woi_0", Hash, "red");
        WriteDownloaded("woi_0", Hash, "green");
        Claim("something_else/abc");

        Assert.Equal(CacheKind.Shipped, Resolve("woi_0")!.Cache);
    }

    [Fact]
    public void AClaimWithNoFileFallsBackToTheShippedCopy()
    {
        // What the game does after the downloaded file is deleted but its line remains: it logs a
        // read failure, then loads the shipped copy.
        WriteShipped("woi_0", Hash, "red");
        Claim($"woi_0/{Hash}");

        var resolved = Resolve("woi_0")!;
        Assert.Equal(CacheKind.Shipped, resolved.Cache);
        Assert.Equal("red", File.ReadAllText(resolved.Path));
    }

    [Fact]
    public void ABrokenClaimIsReportable()
    {
        WriteShipped("woi_0", Hash, "red");
        Claim($"woi_0/{Hash}");
        Assert.Equal(["woi_0"], DownloadedBundleCache.Open(_downloaded).ClaimedButMissing());
    }

    [Fact]
    public void ABundleInNeitherCacheResolvesToNothing()
    {
        Assert.Null(Resolve("woi_0"));
    }
}
