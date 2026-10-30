using System.Text;
using System.Text.RegularExpressions;
using PGAssetTool.Core.Settings;

namespace PGAssetTool.Core.Addressables;

/// What the catalog records about a bundle's bytes: the CRC its contents must hash to, and how long
/// the file is.
public sealed record BundleNumbers(uint Crc, long Size)
{
    /// The CRC that means "do not check this bundle", which no bundle the game ships carries.
    ///
    /// All 17,988 entries have a non-zero CRC, so a zero is this tool's own mark: it both lets the
    /// game load a bundle whose bytes this wrote and leaves the catalog honest about which bundles
    /// are no longer as delivered. See `ModApplier.SettleCatalog`.
    public const uint Unchecked = 0;
}

/// Rewriting what the catalog says about a bundle, which is what makes an edited one loadable.
///
/// The game checks a bundle against the CRC the catalog records for it and throws away one that
/// disagrees — silently, with nothing the player sees but the content missing. So writing a bundle
/// means writing this number with it, and putting a bundle back means putting the number back.
///
/// The numbers live in UTF-16 JSON inside the base64 `m_ExtraDataString` blob, at byte offsets the
/// entry table points to. A number that gets longer or shorter moves every object after it, so the
/// blob is rebuilt and the entry table's offsets rewritten with it — patching the bytes where they
/// lie and leaving the offsets alone is what breaks a catalog.
///
/// Everything outside those two blobs is left byte for byte as it was: the file is 40MB and
/// re-serializing it would rewrite all of it to no purpose.
public static partial class CatalogPatch
{
    /// What the catalog records now for each bundle named, by file name. A bundle it does not hold
    /// is absent rather than zero, so a caller can tell the two apart.
    public static IReadOnlyDictionary<string, BundleNumbers> Read(
        ContentCatalog catalog, IEnumerable<string> fileNames)
    {
        var found = new Dictionary<string, BundleNumbers>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
            if (catalog.ByFileName(name) is { } bundle)
                found[name] = new BundleNumbers(bundle.Crc, bundle.Size);
        return found;
    }

    /// Sets the recorded numbers for the bundles named, and answers how many objects were changed.
    ///
    /// A bundle the catalog does not hold is passed over rather than failing the write: the caller
    /// has a list of what it wrote and the catalog has its own idea of what exists, and the count
    /// is how the two are compared.
    /// <param name="catalog">
    /// The catalog as read from <paramref name="catalogPath"/>, which is where a bundle's file name
    /// is turned into the hash its object is found by. The names cannot be matched without it: most
    /// end in that hash and a few do not.
    /// </param>
    public static int Write(
        string catalogPath, ContentCatalog catalog,
        IReadOnlyDictionary<string, BundleNumbers> wanted)
    {
        if (wanted.Count == 0) return 0;

        var text = File.ReadAllBytes(catalogPath);
        var entries = Blob(text, "m_EntryDataString");
        var extra = Blob(text, "m_ExtraDataString");

        var table = CatalogTables.ReadEntries(Convert.FromBase64String(Text(text, entries)));
        var blob = Convert.FromBase64String(Text(text, extra));

        // Which object belongs to which bundle, by the hash in its own JSON. The catalog is not
        // asked, because what is being rewritten is the catalog.
        var byOffset = new Dictionary<int, string>();
        foreach (var entry in table)
        {
            if (entry.DataIndex < 0 || byOffset.ContainsKey(entry.DataIndex)) continue;
            if (CatalogTables.ReadJsonObject(blob, entry.DataIndex) is not { } json) continue;
            if (HashIn(json) is { } hash) byOffset[entry.DataIndex] = hash;
        }

        var byHash = new Dictionary<string, BundleNumbers>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, numbers) in wanted)
            if (catalog.ByFileName(name) is { } bundle) byHash[bundle.Hash] = numbers;

        // Every object the entry table points at, in the order the blob holds them. An object's
        // extent is from its own offset to the next one, whatever kind it is — which is how the
        // ones this does not understand are carried across untouched.
        var offsets = byOffsetKeys(table);
        var rebuilt = new MemoryStream(blob.Length);
        var moved = new Dictionary<int, int>(offsets.Count);
        var changed = 0;

        // Anything before the first object — there is nothing in practice, but the blob's own start
        // is not this code's to assume.
        if (offsets.Count == 0 || offsets[0] > 0)
            rebuilt.Write(blob.AsSpan(0, offsets.Count == 0 ? blob.Length : offsets[0]));

        for (var i = 0; i < offsets.Count; i++)
        {
            var from = offsets[i];
            var to = i + 1 < offsets.Count ? offsets[i + 1] : blob.Length;
            moved[from] = (int)rebuilt.Position;

            if (byOffset.TryGetValue(from, out var hash)
                && byHash.TryGetValue(hash, out var numbers)
                && CatalogTables.LocateJsonObject(blob, from) is { } json)
            {
                var was = Encoding.Unicode.GetString(blob, json.At, json.Length);
                var now = WithNumbers(was, numbers);
                if (!string.Equals(now, was, StringComparison.Ordinal))
                {
                    var utf16 = Encoding.Unicode.GetBytes(now);

                    // The header, then the new length, then the new JSON; whatever padding followed
                    // the old JSON inside this object's extent is carried across after it.
                    rebuilt.Write(blob.AsSpan(from, json.At - 4 - from));
                    rebuilt.Write(BitConverter.GetBytes(utf16.Length));
                    rebuilt.Write(utf16);
                    var tail = json.At + json.Length;
                    if (to > tail) rebuilt.Write(blob.AsSpan(tail, to - tail));
                    changed++;
                    continue;
                }
            }

            rebuilt.Write(blob.AsSpan(from, to - from));
        }

        if (changed == 0) return 0;

        var written = rebuilt.ToArray();
        for (var i = 0; i < table.Length; i++)
            if (table[i].DataIndex >= 0 && moved.TryGetValue(table[i].DataIndex, out var now))
                table[i] = table[i] with { DataIndex = now };

        // The entry table last, so it is written against the blob that was actually produced.
        var spliced = Splice(text,
            (entries, Convert.ToBase64String(CatalogTables.WriteEntries(table))),
            (extra, Convert.ToBase64String(written)));

        AtomicFile.WriteAllBytes(catalogPath, spliced);
        return changed;
    }

    /// The offsets the entry table points at, once each and in order.
    private static List<int> byOffsetKeys(CatalogTables.Entry[] table)
    {
        var offsets = new SortedSet<int>();
        foreach (var entry in table)
            if (entry.DataIndex >= 0) offsets.Add(entry.DataIndex);
        return [.. offsets];
    }

    /// The same JSON with the two numbers set, and everything else — field order, spacing, the
    /// fields this does not know about — left as it was.
    private static string WithNumbers(string json, BundleNumbers numbers)
    {
        json = Crc().Replace(json, $"\"m_Crc\":{numbers.Crc}", 1);
        return Size().Replace(json, $"\"m_BundleSize\":{numbers.Size}", 1);
    }

    [GeneratedRegex("\"m_Crc\"\\s*:\\s*-?\\d+")]
    private static partial Regex Crc();

    [GeneratedRegex("\"m_BundleSize\"\\s*:\\s*-?\\d+")]
    private static partial Regex Size();

    [GeneratedRegex("\"m_Hash\"\\s*:\\s*\"([0-9a-fA-F]+)\"")]
    private static partial Regex Hash();

    private static string? HashIn(string json)
        => Hash().Match(json) is { Success: true } match ? match.Groups[1].Value : null;

    /// Where one of the base64 blobs sits in the file, as the span between its quotes.
    ///
    /// Found in the raw bytes rather than through a parser: base64 holds no quote and no escape, so
    /// the literal ends at the next quote, and the other forty megabytes are never touched.
    private static (int At, int Length) Blob(byte[] text, string field)
    {
        var needle = Encoding.UTF8.GetBytes($"\"{field}\":\"");
        var at = IndexOf(text, needle);
        if (at < 0)
            throw new InvalidDataException($"The catalog has no '{field}', so it is not one this writes.");

        var from = at + needle.Length;
        var to = Array.IndexOf(text, (byte)'"', from);
        if (to < 0) throw new InvalidDataException($"The catalog's '{field}' is not terminated.");
        return (from, to - from);
    }

    private static string Text(byte[] text, (int At, int Length) span)
        => Encoding.ASCII.GetString(text, span.At, span.Length);

    /// Replaces the given spans with the given text, in one pass. The spans must not overlap.
    private static byte[] Splice(byte[] text, params (
        (int At, int Length) Span, string With)[] parts)
    {
        var ordered = parts.OrderBy(p => p.Span.At).ToArray();
        var output = new MemoryStream(text.Length);
        var at = 0;
        foreach (var (span, with) in ordered)
        {
            output.Write(text.AsSpan(at, span.At - at));
            output.Write(Encoding.ASCII.GetBytes(with));
            at = span.At + span.Length;
        }
        output.Write(text.AsSpan(at));
        return output.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var at = 0; at + needle.Length <= haystack.Length; at++)
        {
            var i = 0;
            while (i < needle.Length && haystack[at + i] == needle[i]) i++;
            if (i == needle.Length) return at;
        }
        return -1;
    }
}
