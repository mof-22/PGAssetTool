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

    [Fact]
    public void AComponentIsNotWrittenBackInAnyFormat()
    {
        // The raw route is what makes every other class writable, so the only thing standing
        // between a component and the game is this answer. It is asked at three doors — converting,
        // building and applying — and all three ask it here.
        Assert.False(Replaceable.CanWriteBack(AssetClassID.MonoBehaviour));

        Assert.True(Replaceable.CanWriteBack(AssetClassID.Texture2D));
        Assert.True(Replaceable.CanWriteBack(AssetClassID.Mesh));
        Assert.True(Replaceable.CanWriteBack(AssetClassID.AudioClip));

        // The classes a mod legitimately reaches through the raw route stay reachable: a material,
        // a shader, a font, and the objects a prefab is built out of.
        foreach (var cls in new[]
                 {
                     AssetClassID.Material, AssetClassID.Shader, AssetClassID.Font,
                     AssetClassID.GameObject, AssetClassID.Transform, AssetClassID.TextMesh,
                 })
            Assert.True(Replaceable.CanWriteBack(cls), $"{cls} should still be writable");
    }

    [Fact]
    public void AComponentIsNotListedEither()
    {
        // Weaker than refusing to write one, and known to be: anything that opens the bundles shows
        // the same thing. What it buys is that this tool is not where somebody first meets the idea.
        Assert.False(Replaceable.CanShow(AssetClassID.MonoBehaviour));
        Assert.False(Replaceable.CanShow(AssetClassID.MonoScript));

        // Everything else is listed, including the classes that cannot be replaced in an editable
        // format. Not being editable is a different thing from not being shown.
        foreach (var cls in new[]
                 {
                     AssetClassID.Texture2D, AssetClassID.Mesh, AssetClassID.AudioClip,
                     AssetClassID.Material, AssetClassID.Shader, AssetClassID.Font,
                     AssetClassID.GameObject, AssetClassID.Transform, AssetClassID.AnimationClip,
                 })
            Assert.True(Replaceable.CanShow(cls), $"{cls} should still be listed");
    }

    [Fact]
    public void TheRefusalSaysWhichFileAndWhy()
    {
        var said = Replaceable.WhyRefused(AssetClassID.MonoBehaviour, "weapon_thing.dat");

        Assert.Contains("weapon_thing.dat", said);
        Assert.Contains("behave", said);
    }
}
