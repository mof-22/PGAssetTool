using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Assets;

/// Reading an asset out of a bundle, whichever route named that bundle.
///
/// The game has two of them at once. The old one addresses a bundle by the name in its manifest and
/// finds it under `AssetBundles_v3`; the Addressables one addresses it by the file name the catalog
/// gives and finds it in the cache the game downloads into or beside the install. The two share
/// nothing — not the directories, not the hashes, not the manifest — but everything downstream of
/// opening a file is the same work on the same bytes: walking references, decoding a texture,
/// assembling a model, writing a glTF.
///
/// So the route is what differs and this is where the two meet. Whoever hands one of these over has
/// already decided which game layout is being read; nothing below here asks.
public interface IBundleReader : IDisposable
{
    /// The installation's own serialized files — `resources.assets`, `globalgamemanagers`, the
    /// levels — which are in no bundle on either route and which some icons still live in.
    IEnumerable<string> SerializedFiles { get; }

    AssetsContext Context { get; }

    /// Every bundle this reader can name. The old route has about 90 of them and the Addressables
    /// route about 18,000, so anything that walks all of them pays very differently on each.
    IReadOnlyCollection<string> BundleNames { get; }

    /// Whether this is a bundle this reader can open at all, as against one of the installation's
    /// own serialized files.
    ///
    /// Something has to tell the two apart, because a few icons live in `resources.assets` rather
    /// than in any bundle and are reached a different way. The name used to say so by itself — a
    /// bundle on the old route has no extension and a data file does — and an Addressables bundle
    /// is named `<name>_<hash>.bundle`, so that rule now calls every one of them a data file.
    bool Knows(string bundle);

    /// Where the bytes are, or what is wrong: a bundle the game has not downloaded and a bundle
    /// missing from the installation are different troubles and are said differently.
    string PathOf(string bundle);

    /// The serialized file in a bundle. Opened once per reader, however often it is asked for: a
    /// second open is a handle that disposing does not close, and the next write to that bundle
    /// then fails with the file in use.
    AssetsFileInstance Open(string bundle);

    /// Texture and audio payloads are not in the serialized object; they sit in a sibling stream
    /// entry that the object points into. This reads that slice.
    byte[] ReadResource(string bundle, string sourcePath, long offset, long size);

    /// Which of these bundles this reader cannot answer for as the game delivered them. Extracting
    /// from one would write somebody else's mod out as the game's own work, so it is refused.
    IReadOnlyList<string> Altered(IEnumerable<string> bundles);
}
