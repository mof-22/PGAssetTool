using System.Security.Cryptography;
using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Tests;

public class CacheConsolidationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-consolidate").FullName;
    private readonly string _shipped;
    private readonly string _downloadedRoot;
    private readonly List<(string Name, string Hash)> _manifest = [];

    public CacheConsolidationTests()
    {
        var data = Path.Combine(_root, "Game_Data");
        _shipped = Path.Combine(data, "StreamingAssets", "Cache", "bundles");
        Directory.CreateDirectory(_shipped);
        File.WriteAllLines(Path.Combine(data, "app.info"), ["Company", "Product"]);
        _downloadedRoot = Path.Combine(_root, "LocalLow", "Cache");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Md5Of(string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    /// The game names a bundle's directory after its MD5, so a "pristine" file is one whose content
    /// hashes to the folder it sits in.
    private void Ship(string bundle, string content, string? underHash = null)
    {
        var hash = underHash ?? Md5Of(content);
        _manifest.RemoveAll(m => m.Name == bundle);
        _manifest.Add((bundle, hash));
        Write(Path.Combine(_shipped, bundle, hash, bundle), content);
    }

    private void Download(string bundle, string content, string? underHash = null)
        => Write(Path.Combine(_downloadedRoot, "bundles", bundle, underHash ?? Md5Of(content), bundle), content);

    private void Claim(params (string Bundle, string Hash)[] entries)
        => Write(Path.Combine(_downloadedRoot, "Info", "names"),
            string.Join('\n', entries.Select(e => $"{e.Bundle}/{e.Hash}")));

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private GameInstallation Game()
    {
        File.WriteAllText(Path.Combine(_shipped, "embedded_asset_bundles.json"),
            "[" + string.Join(',', _manifest.Select(m => $"{{\"Name\":\"{m.Name}\",\"Hash\":\"{m.Hash}\"}}")) + "]");
        return GameInstallation.OpenWith(_root, DownloadedBundleCache.Open(_downloadedRoot));
    }

    private ConsolidationVerdict VerdictFor(string bundle)
        => CacheConsolidation.Plan(Game()).Single(i => i.Bundle == bundle).Verdict;

    [Fact]
    public void ACopyIdenticalToTheShippedOneIsRedundant()
    {
        Ship("a", "same");
        Download("a", "same");
        Claim(("a", Md5Of("same")));
        Assert.Equal(ConsolidationVerdict.Redundant, VerdictFor("a"));
    }

    [Fact]
    public void ADamagedDownloadIsRecognisedByTheShippedCopyStillMatchingItsHash()
    {
        var hash = Md5Of("good");
        Ship("a", "good");
        Download("a", "truncated", underHash: hash);
        Claim(("a", hash));
        Assert.Equal(ConsolidationVerdict.DownloadedIsDamaged, VerdictFor("a"));
    }

    [Fact]
    public void AnEditedShippedCopyIsTheOneCarryingAMod()
    {
        var hash = Md5Of("original");
        Ship("a", "modded", underHash: hash);
        Download("a", "original", underHash: hash);
        Claim(("a", hash));
        Assert.Equal(ConsolidationVerdict.ShippedCarriesAMod, VerdictFor("a"));
    }

    [Fact]
    public void ADifferentVersionIsNeverRemovedAutomatically()
    {
        Ship("a", "v1");
        Download("a", "v2");
        Claim(("a", Md5Of("v2")));

        var item = CacheConsolidation.Plan(Game()).Single();
        Assert.Equal(ConsolidationVerdict.NewerVersion, item.Verdict);
        Assert.False(item.SafeToRemove);
    }

    [Fact]
    public void ContentThatNeverShippedIsNeverRemovedAutomatically()
    {
        Ship("other", "x");
        _manifest.Add(("a", Md5Of("only")));
        Download("a", "only");
        Claim(("a", Md5Of("only")));

        var item = CacheConsolidation.Plan(Game()).Single(i => i.Bundle == "a");
        Assert.Equal(ConsolidationVerdict.OnlyHere, item.Verdict);
        Assert.False(item.SafeToRemove);
    }

    [Fact]
    public void AClaimWithNoFileIsSafeToDrop()
    {
        Ship("a", "shipped");
        Claim(("a", _manifest.Single().Hash));
        Assert.Equal(ConsolidationVerdict.BrokenClaim, VerdictFor("a"));
    }

    [Fact]
    public void ApplyingRemovesOnlyTheSafeOnesAndRewritesTheLedger()
    {
        Ship("redundant", "same");
        Download("redundant", "same");
        Ship("newer", "v1");
        Download("newer", "v2");
        Claim(("redundant", Md5Of("same")), ("newer", Md5Of("v2")));

        var game = Game();
        var removed = CacheConsolidation.Apply(game, CacheConsolidation.Plan(game));

        Assert.Equal(["redundant"], removed.Select(r => r.Bundle));
        Assert.False(Directory.Exists(Path.Combine(_downloadedRoot, "bundles", "redundant")));
        Assert.True(File.Exists(Path.Combine(_downloadedRoot, "bundles", "newer", Md5Of("v2"), "newer")));

        // The ledger must stop claiming what is gone, or the game logs a read failure for it.
        var ledger = File.ReadAllLines(Path.Combine(_downloadedRoot, "Info", "names"));
        Assert.Equal([$"newer/{Md5Of("v2")}"], ledger);
    }
}
