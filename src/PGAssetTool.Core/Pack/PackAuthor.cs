using System.Security.Cryptography;

namespace PGAssetTool.Core.Pack;

/// The key a pack is signed with, and the name that goes beside it.
///
/// Made once and kept beside everything else the tool keeps, so carrying the folder carries the
/// identity. Losing it means later packs are signed by a different key and everyone who has the old
/// one sees them as somebody else's — which is the point of a signature, and also the reason it is
/// worth backing up along with the mods.
///
/// P-256 with SHA-256, because it is in the framework. Nothing here needs a dependency, and a
/// signature scheme is the last place to take one on.
public sealed class PackAuthor : IDisposable
{
    public const string FileName = "author.key";

    private readonly ECDsa _key;

    private PackAuthor(ECDsa key) => _key = key;

    /// The key beside the tool, made if it is not there yet.
    public static PackAuthor Mine(string? home = null)
    {
        var directory = home ?? Mods.ModStore.DefaultHome();
        var path = Path.Combine(directory, FileName);

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            try
            {
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
                return new PackAuthor(key);
            }
            catch (CryptographicException)
            {
                // A key that cannot be read is not a reason to stop; it is a reason to make one.
                // Keeping the old file means whoever knows what it was can still recover it.
                File.Move(path, path + ".unreadable", overwrite: true);
                key.Dispose();
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            }
        }

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
        return new PackAuthor(key);
    }

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    /// Short enough to read out loud, long enough that nobody will collide with it by accident.
    /// An author who publishes theirs lets people tell a re-signed pack from a genuine one.
    public string Fingerprint => FingerprintOf(PublicKey);

    public static string FingerprintOf(byte[] publicKey)
        => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..16];

    public byte[] Sign(byte[] contents) => _key.SignData(contents, HashAlgorithmName.SHA256);

    public static bool Verifies(byte[] contents, byte[] signature, byte[] publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(contents, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}
