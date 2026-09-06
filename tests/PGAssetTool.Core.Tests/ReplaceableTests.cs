using AssetsTools.NET.Extra;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

/// The list is consulted when building a manifest, when applying an operation, and when deciding
/// what a tree shows. These pin it as one list rather than three that drift.
public class ReplaceableTests
{
    [Theory]
    [InlineData("png", PackOperations.ReplaceTexture)]
    [InlineData("PNG", PackOperations.ReplaceTexture)]
    [InlineData("glb", PackOperations.ReplaceMesh)]
    [InlineData("wav", PackOperations.ReplaceAudio)]
    [InlineData("mp3", PackOperations.ReplaceAudio)]
    [InlineData("ogg", PackOperations.ReplaceAudio)]
    public void EachAuthoringFormatNamesTheOperationThatTakesIt(string format, string expected)
        => Assert.Equal(expected, Replaceable.OperationForFormat(format));

    [Fact]
    public void AFormatWithNoImportPathIsNotOfferedAtAll()
    {
        // A workspace listing one of these would produce a pack that fails when applied. A field
        // dump is readable and nothing more: there is no path from an edited one back into a bundle.
        Assert.Null(Replaceable.OperationForFormat("json"));
        Assert.Null(Replaceable.OperationForFormat(""));
    }

    [Fact]
    public void RawBytesAreTheirOwnOperation()
    {
        // Not an interchange format, and deliberately not one of the Kinds: it is how a class with
        // no editor gets written back, which is a different claim from being editable.
        Assert.Equal(PackOperations.ReplaceRaw, Replaceable.OperationForFormat(Replaceable.RawFormat));
        Assert.Equal(PackOperations.ReplaceRaw, Replaceable.OperationForFormat("DAT"));
    }

    [Fact]
    public void TheClassesMatchTheFormats()
    {
        Assert.True(Replaceable.Supports(AssetClassID.Texture2D));
        Assert.True(Replaceable.Supports(AssetClassID.Mesh));
        Assert.True(Replaceable.Supports(AssetClassID.AudioClip));

        // These have no format an author can edit them in. Their bytes can still be written back
        // wholesale — that is what replaceRaw is for — and Supports says nothing about that.
        Assert.False(Replaceable.Supports(AssetClassID.Shader));
        Assert.False(Replaceable.Supports(AssetClassID.MonoBehaviour));
        Assert.False(Replaceable.Supports(AssetClassID.Material));
    }

    [Fact]
    public void EveryOperationTheApplierKnowsIsReachableFromSomeFormat()
    {
        // If an operation exists with no format mapping to it, nothing can ever produce one.
        var reachable = new[] { "png", "glb", "wav", "mp3", "ogg" }
            .Select(Replaceable.OperationForFormat)
            .ToHashSet();

        Assert.Contains(PackOperations.ReplaceTexture, reachable);
        Assert.Contains(PackOperations.ReplaceMesh, reachable);
        Assert.Contains(PackOperations.ReplaceAudio, reachable);
        Assert.Equal(Replaceable.Classes.Count, reachable.Count);
    }
}
