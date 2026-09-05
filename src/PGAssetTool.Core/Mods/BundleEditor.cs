using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Mods;

/// Opens one bundle for modification and writes it back out.
///
/// Held separately from the read-only BundleSet so that editing never disturbs the caches the rest
/// of the tool reads through.
public sealed class BundleEditor : IDisposable
{
    private readonly AssetsContext _context = new();
    private readonly BundleFileInstance _bundle;
    private readonly int _entryIndex;
    private readonly Dictionary<int, byte[]> _streams = [];

    public BundleEditor(string bundlePath)
    {
        _bundle = _context.OpenBundle(bundlePath);
        var entry = AssetsContext.SerializedEntries(_bundle).First();
        _entryIndex = entry.Index;
        File = _context.OpenBundleEntry(_bundle, _entryIndex);
    }

    public AssetsFileInstance File { get; }
    public AssetsContext Context => _context;

    public AssetTypeValueField? Read(AssetFileInfo info) => _context.Deserialize(File, info);

    public void Stage(AssetFileInfo info, AssetTypeValueField field) => info.SetNewData(field);

    /// Reads a slice of one of the bundle's stream entries — where texture pixels and audio banks
    /// actually live, since the object only points at them.
    public byte[]? ReadStream(string sourcePath, long offset, long size)
    {
        var wanted = Path.GetFileName(sourcePath);
        var entry = _bundle.file.BlockAndDirInfo.DirectoryInfos
            .FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (entry is null || size <= 0) return null;

        var reader = _bundle.file.DataReader;
        reader.Position = entry.Offset + offset;
        return reader.ReadBytes((int)size);
    }


    /// Appends bytes to one of the bundle's stream entries, returning the offset they landed at.
    ///
    /// An AudioClip does not carry its own payload; it points at a byte range in a sibling
    /// .resource entry that every clip in the bundle shares. Overwriting that range in place would
    /// only work while the replacement is no larger, so the new bank goes on the end and the object
    /// is repointed at it. The bundle grows by the size of the clip, but applying a mod always
    /// rebuilds the bundle from its untouched backup, so the growth never compounds.
    public long AppendToStream(string sourcePath, byte[] payload, int alignment = 32)
    {
        var wanted = Path.GetFileName(sourcePath);
        var entries = _bundle.file.BlockAndDirInfo.DirectoryInfos;
        var index = entries.FindIndex(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new FileNotFoundException($"The bundle has no stream entry '{wanted}'.");

        if (!_streams.TryGetValue(index, out var current))
        {
            var reader = _bundle.file.DataReader;
            reader.Position = entries[index].Offset;
            current = reader.ReadBytes((int)entries[index].DecompressedSize);
        }

        // The game's own clips all start on a 32-byte boundary, and FMOD reads the bank as a block.
        var offset = (current.Length + alignment - 1) / alignment * alignment;
        var grown = new byte[offset + payload.Length];
        current.CopyTo(grown, 0);
        payload.CopyTo(grown, offset);
        _streams[index] = grown;

        return offset;
    }

    /// Writes the whole bundle out, LZ4-compressed as the game ships it. Uncompressed would also
    /// load, but every bundle here is compressed and there is no reason to be the odd one out.
    public void Save(string outputPath)
    {
        var directory = _bundle.file.BlockAndDirInfo.DirectoryInfos;
        directory[_entryIndex].SetNewData(Serialize(w => File.file.Write(w, 0)));
        foreach (var (index, payload) in _streams) directory[index].SetNewData(payload);
        var uncompressed = new MemoryStream(Serialize(w => _bundle.file.Write(w, -1)));

        // Packing reads from the rebuilt file as it writes, so the source stream outlives this call
        // rather than being disposed with the reader.
        var rebuilt = new AssetBundleFile();
        rebuilt.Read(new AssetsFileReader(uncompressed));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var writer = new AssetsFileWriter(outputPath))
            rebuilt.Pack(writer, AssetBundleCompressionType.LZ4);

        rebuilt.Close();
        uncompressed.Dispose();
    }

    private static byte[] Serialize(Action<AssetsFileWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = new AssetsFileWriter(stream);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose() => _context.Dispose();
}
