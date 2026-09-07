using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGAssetTool.Core.Pack;

/// What is known about who built a pack and whether it still says what they wrote.
public sealed record PackSeal(bool Protected, string Author, string Fingerprint, SealState State)
{
    public static PackSeal Plain { get; } = new(false, "", "", SealState.Unsigned);

    public string Describe => State switch
    {
        SealState.Signed when Author.Length > 0 => $"signed by {Author} ({Fingerprint})",
        SealState.Signed => $"signed ({Fingerprint})",
        SealState.Altered => "changed since it was built",
        _ => "not signed",
    };
}

public enum SealState
{
    /// No signature at all: an older pack, or one built with protection off.
    Unsigned,

    /// The contents are what the holder of that key put in.
    Signed,

    /// There is a signature and the contents no longer match it.
    Altered,
}

/// A .pgmod on disk, in either of the two shapes it comes in.
///
/// Plainly, it is a zip. Protected, it is a small container: a readable header saying who built it
/// and a payload that is the same zip with a keystream over it. The header stays readable because
/// the manager wants to say who a pack came from without unpacking it, and because a file that
/// gives no account of itself is worse than one that does.
///
/// The scrambling stops a pack being renamed to .zip and opened. It stops nothing else — this
/// tool's source says exactly how to undo it — and it is not meant to. What it is for is that
/// somebody's work is not casually lifted out of the file they published.
public static class PackFile
{
    private static readonly byte[] Magic = "PGMOD"u8.ToArray();

    /// The shape of the container, written straight after the magic.
    ///
    /// It has been there since the first protected pack, but as a control character inside the
    /// magic's own string literal, where nothing showed it: not the editor, not a diff, not a
    /// grep for the constant. Anything that strips control characters from a source file — a
    /// careless paste, a helpful tool — would have quietly changed the magic and left every pack
    /// built afterwards unreadable by every build before it, with no line in the diff to say so.
    /// Written out, it is a version byte, which is what it always was.
    private const byte Format = 1;

    /// Fixed, and deliberately so: any build of this tool has to be able to open any pack. A key
    /// only the author knew would make a pack nobody else could install, which is a different
    /// feature and not this one.
    private static readonly byte[] Seed = "PGAssetTool pack payload"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Header(string Author, string PublicKey, string Signature);

    /// Reads a pack, whichever shape it is in. The archive owns the stream it was opened from.
    public static ZipArchive Open(string path) => new(new MemoryStream(Contents(path)), ZipArchiveMode.Read);

    /// The zip inside a pack, unscrambled if it needed to be.
    public static byte[] Contents(string path)
    {
        var raw = File.ReadAllBytes(path);
        return Split(raw) is var (_, _, payload) ? payload : raw;
    }

    /// Who built it and whether it still matches, without unpacking anything.
    public static PackSeal Inspect(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            if (Split(raw) is not var (_, header, payload)) return PackSeal.Plain;

            var publicKey = Convert.FromBase64String(header.PublicKey);
            var state = PackAuthor.Verifies(payload, Convert.FromBase64String(header.Signature), publicKey)
                ? SealState.Signed
                : SealState.Altered;

            return new PackSeal(true, header.Author, PackAuthor.FingerprintOf(publicKey), state);
        }
        catch (Exception e) when (e is IOException or FormatException or JsonException or ArgumentException)
        {
            return PackSeal.Plain;
        }
    }

    /// Writes a pack: the zip as it is, or wrapped and signed.
    public static void Write(string path, byte[] zip, PackAuthor? signer, string author)
    {
        if (signer is null)
        {
            File.WriteAllBytes(path, zip);
            return;
        }

        var header = JsonSerializer.SerializeToUtf8Bytes(
            new Header(author, Convert.ToBase64String(signer.PublicKey),
                Convert.ToBase64String(signer.Sign(zip))),
            Json);

        using var file = File.Create(path);
        file.Write(Magic);
        file.WriteByte(Format);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
        file.Write(length);
        file.Write(header);

        // Signed over the plain contents, scrambled on the way out: the signature says what the
        // pack holds, not how it happens to be stored.
        file.Write(Scramble((byte[])zip.Clone()));
    }

    /// Splits a protected pack into what it says about itself and what it holds. Null for a plain
    /// zip, which is every pack built before this existed and every one built with protection off.
    private static (byte Version, Header Header, byte[] Payload)? Split(byte[] raw)
    {
        if (raw.Length < Magic.Length + 5 || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;

        // A version this build does not know is not a pack it can speak for. Refusing here rather
        // than reading it anyway means a later format is a plain "not signed" to an older build,
        // never a wrong answer about who wrote it.
        var version = raw[Magic.Length];
        if (version != Format) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(Magic.Length + 1));
        var at = Magic.Length + 1 + 4;
        if (length < 0 || at + length > raw.Length) return null;

        var header = JsonSerializer.Deserialize<Header>(raw.AsSpan(at, length), Json);
        if (header is null) return null;

        return (version, header, Scramble(raw[(at + length)..]));
    }

    /// Its own inverse: the same keystream over the bytes puts them back.
    private static byte[] Scramble(byte[] bytes)
    {
        Span<byte> counter = stackalloc byte[Seed.Length + 4];
        Seed.CopyTo(counter);

        var block = new byte[SHA256.HashSizeInBytes];
        for (var at = 0; at < bytes.Length; at += block.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(counter[Seed.Length..], at / block.Length);
            SHA256.HashData(counter, block);

            var run = Math.Min(block.Length, bytes.Length - at);
            for (var i = 0; i < run; i++) bytes[at + i] ^= block[i];
        }
        return bytes;
    }
}
