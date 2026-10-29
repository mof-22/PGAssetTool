using AssetsTools.NET;
using K4os.Compression.LZ4;

namespace PGAssetTool.Core.Addressables;

/// The CRC a catalog records for a bundle, and which the game checks a cached bundle against.
///
/// It is a plain CRC-32 — the usual reflected polynomial, all ones to start, inverted at the end —
/// taken over the **concatenated decompressed block data**: the header and the block and directory
/// table are not in it. Measured against 5,174 bundles whose CRC the game's own catalog gives, 4,136
/// of them cached and 1,038 shipped: every one agrees.
///
/// A bundle whose blocks will not decompress has no data to hash, so the answer is the CRC of
/// nothing, which is zero — that is what the game reports for a bundle with a byte flipped in it.
///
/// This matters because a rewritten bundle whose catalog CRC still describes the old bytes is thrown
/// away and fetched again, with no error the player sees. Writing a bundle means writing this
/// number with it.
public static class BundleCrc
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// The CRC-32 of a run of bytes.
    public static uint Of(ReadOnlySpan<byte> data)
    {
        var running = new Running();
        running.Add(data);
        return running.Value;
    }

    /// A CRC taken a piece at a time, for data that arrives in pieces and is not worth gathering:
    /// a bundle is hashed block by block as it decompresses, and a 6MB weapon-map bundle comes to
    /// 99MB whole.
    public sealed class Running
    {
        private uint _state = 0xFFFFFFFFu;

        public void Add(ReadOnlySpan<byte> data)
        {
            var state = _state;
            foreach (var b in data) state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
            _state = state;
        }

        public uint Value => _state ^ 0xFFFFFFFFu;
    }

    /// The CRC the catalog should carry for this bundle file, or null for one compressed a way this
    /// does not decode.
    public static uint? OfBundle(string path)
    {
        var bundle = new AssetBundleFile();
        using var reader = new AssetsFileReader(File.OpenRead(path));
        bundle.Read(reader);

        var blocks = bundle.BlockAndDirInfo.BlockInfos;

        // The low six bits of a block's flags say how it is compressed: 0 none, 1 LZMA, 2 LZ4,
        // 3 LZ4HC. LZMA is left to the library here, as it is everywhere else in the tool.
        if (blocks.Any(b => (b.Flags & 0x3F) is not (0 or 2 or 3))) return null;

        using var file = File.OpenRead(path);
        file.Position = bundle.Header.GetFileDataOffset();

        var running = new Running();
        byte[]? packed = null;
        byte[]? unpacked = null;
        foreach (var block in blocks)
        {
            if (packed is null || packed.Length < block.CompressedSize)
                packed = new byte[block.CompressedSize];
            file.ReadExactly(packed, 0, (int)block.CompressedSize);

            if ((block.Flags & 0x3F) == 0)
            {
                running.Add(packed.AsSpan(0, (int)block.CompressedSize));
                continue;
            }

            if (unpacked is null || unpacked.Length < block.DecompressedSize)
                unpacked = new byte[block.DecompressedSize];
            var written = LZ4Codec.Decode(
                packed.AsSpan(0, (int)block.CompressedSize),
                unpacked.AsSpan(0, (int)block.DecompressedSize));
            if (written != block.DecompressedSize) return null;
            running.Add(unpacked.AsSpan(0, written));
        }
        return running.Value;
    }
}
