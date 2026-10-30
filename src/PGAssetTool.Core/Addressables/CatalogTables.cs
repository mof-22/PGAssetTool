using System.Buffers.Binary;
using System.Text;

namespace PGAssetTool.Core.Addressables;

/// The four base64 blobs an Addressables catalog keeps its locator in, read as they are written.
///
/// A catalog is JSON on the outside and packed binary on the inside: the keys, one bucket per key,
/// one entry per location, and a blob of per-location objects the entries point into by byte offset.
/// Nothing here is documented by the engine, so each reader says what the bytes are.
internal static class CatalogTables
{
    /// How an object in the key or extra-data blob says what it is.
    private enum ObjectKind : byte
    {
        AsciiString = 0,
        UnicodeString = 1,
        UInt16 = 2,
        UInt32 = 3,
        Int32 = 4,
        Hash128 = 5,
        Type = 6,
        JsonObject = 7,
    }

    /// One location: the seven numbers the entry table holds for it, in the order it holds them.
    /// <paramref name="DataIndex"/> is a byte offset into the extra-data blob, or -1 for a location
    /// that carries no object of its own — which is most of them, only a bundle having options.
    internal readonly record struct Entry(
        int InternalId, int Provider, int DependencyKey, int DependencyHash,
        int DataIndex, int PrimaryKey, int ResourceType);

    /// One bucket: where its key sits in the key blob, and the locations filed under it.
    internal readonly record struct Bucket(int KeyOffset, int[] Entries);

    internal static Entry[] ReadEntries(byte[] data)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        var entries = new Entry[count];
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * 28;
            entries[i] = new Entry(
                Int32At(data, at), Int32At(data, at + 4), Int32At(data, at + 8), Int32At(data, at + 12),
                Int32At(data, at + 16), Int32At(data, at + 20), Int32At(data, at + 24));
        }
        return entries;
    }

    internal static Bucket[] ReadBuckets(byte[] data)
    {
        var count = Int32At(data, 0);
        var buckets = new Bucket[count];
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            var keyOffset = Int32At(data, at);
            var length = Int32At(data, at + 4);
            at += 8;
            var entries = new int[length];
            for (var e = 0; e < length; e++) entries[e] = Int32At(data, at + e * 4);
            at += length * 4;
            buckets[i] = new Bucket(keyOffset, entries);
        }
        return buckets;
    }

    /// The key at an offset, as text. Numeric keys are the hash codes Addressables files a
    /// dependency list under, and they are wanted as text only to tell one from another.
    internal static string ReadKey(byte[] keys, int at)
    {
        var kind = (ObjectKind)keys[at];
        at++;
        return kind switch
        {
            // A standalone string carries a four-byte length, in bytes rather than characters.
            ObjectKind.AsciiString => Encoding.ASCII.GetString(keys, at + 4, Int32At(keys, at)),
            ObjectKind.UnicodeString => Encoding.Unicode.GetString(keys, at + 4, Int32At(keys, at)),
            ObjectKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(keys.AsSpan(at)).ToString(),
            ObjectKind.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(keys.AsSpan(at)).ToString(),
            ObjectKind.Int32 => Int32At(keys, at).ToString(),

            // A hash is the one string written with a single-byte length.
            ObjectKind.Hash128 => Encoding.ASCII.GetString(keys, at + 1, keys[at]),
            _ => "",
        };
    }

    /// The JSON of the object at a byte offset in the extra-data blob, or null for anything else
    /// there.
    ///
    /// An object is a kind byte, then — for a JSON object — the assembly and class names of the
    /// type it deserializes into, each with a **single-byte** length, and then the JSON itself with
    /// a four-byte length. Both lengths count bytes, and the JSON is UTF-16, so its length is twice
    /// the number of characters.
    internal static string? ReadJsonObject(byte[] extra, int at)
    {
        if (at < 0 || at >= extra.Length) return null;
        if ((ObjectKind)extra[at] != ObjectKind.JsonObject) return null;
        at++;
        at += 1 + extra[at];   // assembly name
        at += 1 + extra[at];   // class name
        var length = Int32At(extra, at);
        at += 4;
        return length >= 0 && at + length <= extra.Length
            ? Encoding.Unicode.GetString(extra, at, length)
            : null;
    }

    /// Where the JSON of the object at a byte offset begins, and how many bytes it runs to. Writing
    /// a value back in place needs these; reading it does not.
    internal static (int At, int Length)? LocateJsonObject(byte[] extra, int at)
    {
        if (at < 0 || at >= extra.Length) return null;
        if ((ObjectKind)extra[at] != ObjectKind.JsonObject) return null;
        at++;
        at += 1 + extra[at];
        at += 1 + extra[at];
        var length = Int32At(extra, at);
        at += 4;
        return length >= 0 && at + length <= extra.Length ? (at, length) : null;
    }

    /// The entry table as the catalog holds it, for writing one back. Every entry is the same seven
    /// numbers in the same order; only the offsets into the extra-data blob ever change, and they
    /// change whenever that blob is rebuilt.
    internal static byte[] WriteEntries(Entry[] entries)
    {
        var data = new byte[4 + entries.Length * 28];
        BinaryPrimitives.WriteInt32LittleEndian(data, entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var at = 4 + i * 28;
            var e = entries[i];
            foreach (var number in new[]
                     {
                         e.InternalId, e.Provider, e.DependencyKey, e.DependencyHash,
                         e.DataIndex, e.PrimaryKey, e.ResourceType,
                     })
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), number);
                at += 4;
            }
        }
        return data;
    }

    private static int Int32At(byte[] data, int at)
        => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
}
