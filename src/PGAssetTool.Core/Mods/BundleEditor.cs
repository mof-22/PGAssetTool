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

    /// Writes the whole bundle out, LZ4-compressed as the game ships it. Uncompressed would also
    /// load, but every bundle here is compressed and there is no reason to be the odd one out.
    public void Save(string outputPath)
    {
        _bundle.file.BlockAndDirInfo.DirectoryInfos[_entryIndex].SetNewData(Serialize(w => File.file.Write(w, 0)));
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
