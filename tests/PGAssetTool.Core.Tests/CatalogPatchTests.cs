using PGAssetTool.Core.Addressables;

namespace PGAssetTool.Core.Tests;

/// Rewriting what the catalog records about a bundle's bytes.
///
/// The game throws away a bundle that does not hash to the number here and says nothing about it,
/// so a mod that leaves this behind simply does not appear. Everything below is checked by reading
/// the written catalog again rather than by comparing bytes: what matters is that the catalog still
/// parses and now says the new numbers.
public class CatalogPatchTests
{
    [Fact]
    public void WhatTheCatalogRecordsIsRead()
    {
        var read = CatalogPatch.Read(
            CatalogFixture.Written(),
            [CatalogFixture.RemoteFileName, CatalogFixture.LocalFileName, "nothing.bundle"]);

        Assert.Equal(new BundleNumbers(4567, 89_012), read[CatalogFixture.RemoteFileName]);
        Assert.Equal(new BundleNumbers(7654, 21_098), read[CatalogFixture.LocalFileName]);
        Assert.False(read.ContainsKey("nothing.bundle"));
    }

    [Fact]
    public void ANumberWrittenIsTheNumberReadBack()
    {
        using var catalog = new Written();

        Assert.Equal(1, CatalogPatch.Write(
            catalog.Path, catalog.Read(),
            new Dictionary<string, BundleNumbers>
            {
                [CatalogFixture.RemoteFileName] = new(3_806_386_998, 7_654_321),
            }));

        var after = catalog.Read();
        var bundle = after.ByFileName(CatalogFixture.RemoteFileName)!;
        Assert.Equal(3_806_386_998u, bundle.Crc);
        Assert.Equal(7_654_321, bundle.Size);
    }

    /// The number lives in UTF-16 JSON inside a blob the entry table points into by byte offset, so
    /// a number of a different length moves every object after it. Writing the longest possible CRC
    /// over the shortest is the case that catches offsets left alone: the second bundle is the one
    /// that moves, and the asset that needs both still has to find them.
    [Fact]
    public void ALongerNumberMovesWhatFollowsItAndNothingIsLost()
    {
        using var catalog = new Written();

        CatalogPatch.Write(catalog.Path, catalog.Read(), new Dictionary<string, BundleNumbers>
        {
            [CatalogFixture.RemoteFileName] = new(4_294_967_295, 9_007_199_254_740_991),
        });

        var after = catalog.Read();

        Assert.Equal(4_294_967_295u, after.ByFileName(CatalogFixture.RemoteFileName)!.Crc);
        Assert.Equal(9_007_199_254_740_991, after.ByFileName(CatalogFixture.RemoteFileName)!.Size);

        // The one that did not change still reads, which it would not if its offset had been left
        // pointing into the middle of the object that grew.
        var untouched = after.ByFileName(CatalogFixture.LocalFileName)!;
        Assert.Equal(7654u, untouched.Crc);
        Assert.Equal(21_098, untouched.Size);
        Assert.Equal("cache_two", untouched.CacheName);

        // And the address still reaches both of them, in the same order.
        Assert.Equal(
            [CatalogFixture.RemoteFileName, CatalogFixture.LocalFileName],
            after.BundlesFor(CatalogFixture.Address).Select(b => b.FileName));
    }

    /// The other direction, for the same reason: a shorter number leaves a gap, and an offset that
    /// was not moved back lands past the object it names.
    [Fact]
    public void AShorterNumberToo()
    {
        using var catalog = new Written();

        CatalogPatch.Write(catalog.Path, catalog.Read(), new Dictionary<string, BundleNumbers>
        {
            [CatalogFixture.RemoteFileName] = new(0, 1),
        });

        var after = catalog.Read();
        Assert.Equal(0u, after.ByFileName(CatalogFixture.RemoteFileName)!.Crc);
        Assert.Equal(1, after.ByFileName(CatalogFixture.RemoteFileName)!.Size);
        Assert.Equal(7654u, after.ByFileName(CatalogFixture.LocalFileName)!.Crc);
        Assert.Equal(2, after.Bundles.Count);
    }

    [Fact]
    public void BothBundlesAtOnce()
    {
        using var catalog = new Written();

        Assert.Equal(2, CatalogPatch.Write(catalog.Path, catalog.Read(), new Dictionary<string, BundleNumbers>
        {
            [CatalogFixture.RemoteFileName] = new(111, 222),
            [CatalogFixture.LocalFileName] = new(333_333_333, 444_444_444),
        }));

        var after = catalog.Read();
        Assert.Equal(111u, after.ByFileName(CatalogFixture.RemoteFileName)!.Crc);
        Assert.Equal(222, after.ByFileName(CatalogFixture.RemoteFileName)!.Size);
        Assert.Equal(333_333_333u, after.ByFileName(CatalogFixture.LocalFileName)!.Crc);
        Assert.Equal(444_444_444, after.ByFileName(CatalogFixture.LocalFileName)!.Size);
    }

    /// Writing the numbers a bundle already has changes nothing, so a reconcile that finds every
    /// bundle already correct does not rewrite a forty-megabyte file.
    [Fact]
    public void WritingWhatIsAlreadyThereChangesNothing()
    {
        using var catalog = new Written();
        var before = File.ReadAllBytes(catalog.Path);

        Assert.Equal(0, CatalogPatch.Write(catalog.Path, catalog.Read(), new Dictionary<string, BundleNumbers>
        {
            [CatalogFixture.RemoteFileName] = new(4567, 89_012),
        }));

        Assert.Equal(before, File.ReadAllBytes(catalog.Path));
    }

    /// A bundle the catalog does not hold is passed over. The caller knows what it wrote and the
    /// catalog knows what exists; the count is how the two are compared.
    [Fact]
    public void ABundleTheCatalogDoesNotHoldIsPassedOver()
    {
        using var catalog = new Written();

        Assert.Equal(0, CatalogPatch.Write(catalog.Path, catalog.Read(), new Dictionary<string, BundleNumbers>
        {
            ["something_else_0000000000000000000000000000ffff.bundle"] = new(1, 2),
        }));
        Assert.Equal(2, catalog.Read().Bundles.Count);
    }

    [Fact]
    public void NothingAskedForIsNothingWritten()
        => Assert.Equal(0, CatalogPatch.Write("no such file", CatalogFixture.Written(), new Dictionary<string, BundleNumbers>()));

    [Fact]
    public void ACatalogWithoutTheBlobsIsOneKindOfTrouble()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgat-patch-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"m_LocatorId":"nothing much"}""");
        try
        {
            var trouble = Assert.Throws<InvalidDataException>(() => CatalogPatch.Write(
                path, CatalogFixture.Written(),
                new Dictionary<string, BundleNumbers> { [CatalogFixture.RemoteFileName] = new(1, 2) }));
            Assert.Contains("m_EntryDataString", trouble.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// A catalog on disk, as the game keeps one.
    private sealed class Written : IDisposable
    {
        public Written()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"pgat-patch-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, CatalogFixture.Json());
        }

        public string Path { get; }

        public ContentCatalog Read() => ContentCatalog.Read(Path);

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
    }
}
