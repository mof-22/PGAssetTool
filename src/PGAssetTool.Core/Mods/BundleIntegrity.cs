using System.Security.Cryptography;

namespace PGAssetTool.Core.Mods;

/// The cache directory a bundle sits in is named after the bundle's MD5, so a file whose hash no
/// longer matches its directory has been modified since the game downloaded it.
///
/// That makes it cheap to tell a pristine bundle from one something has already edited, which
/// matters before taking a backup: recording someone else's modification as the original would
/// destroy the only route back to the shipped file.
public static class BundleIntegrity
{
    public static string Md5(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    public static bool IsPristine(string path, string expectedHash)
        => string.Equals(Md5(path), expectedHash, StringComparison.OrdinalIgnoreCase);
}
