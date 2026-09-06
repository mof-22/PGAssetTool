using AssetsTools.NET;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

/// A pack records where a pointer is, not what it holds, so finding the place again is the whole
/// mechanism. These pin the two halves against each other: whatever All names, Resolve has to reach.
public class PointerPathTests
{
    private static AssetTypeValueField Node(string name, params AssetTypeValueField[] children)
        => new()
        {
            TemplateField = new AssetTypeTemplateField { Name = name, Type = name, Children = [] },
            Children = [.. children],
        };

    private static AssetTypeValueField Array(string name, params AssetTypeValueField[] items)
    {
        var field = Node(name, items);
        field.TemplateField.IsArray = true;
        return field;
    }

    private static AssetTypeValueField Scalar(string name, AssetTypeValue value)
        => new()
        {
            TemplateField = new AssetTypeTemplateField { Name = name, Type = name, Children = [] },
            Children = [],
            Value = value,
        };

    private static AssetTypeValueField Pointer(string name, int fileId, long pathId)
        => Node(name, Scalar("m_FileID", new AssetTypeValue(fileId)),
                      Scalar("m_PathID", new AssetTypeValue(pathId)));

    /// A material's shape, near enough: a pointer at the top, and more inside a list.
    private static AssetTypeValueField Material() => Node(
        "Material",
        Scalar("m_Name", new AssetTypeValue("mat")),
        Pointer("m_Shader", 0, -100),
        Node("m_SavedProperties",
            Array("m_TexEnvs",
                Node("data",
                    Scalar("first", new AssetTypeValue("_MainTex")),
                    Node("second", Pointer("m_Texture", 3, 77))),
                Node("data",
                    Scalar("first", new AssetTypeValue("_BumpMap")),
                    Node("second", Pointer("m_Texture", 0, -200))))));

    [Fact]
    public void EveryPointerIsFoundAndNamed()
    {
        var found = PointerPath.All(Material()).Select(p => p.Path).ToList();

        Assert.Equal(
            [
                "m_Shader",
                "m_SavedProperties/m_TexEnvs/0/second/m_Texture",
                "m_SavedProperties/m_TexEnvs/1/second/m_Texture",
            ],
            found);
    }

    [Fact]
    public void WhatIsFoundCanBeReachedAgain()
    {
        var material = Material();
        foreach (var (path, pointer) in PointerPath.All(material).ToList())
        {
            var again = PointerPath.Resolve(material, path);
            Assert.NotNull(again);
            Assert.Equal(pointer["m_PathID"].AsLong, again["m_PathID"].AsLong);
        }
    }

    [Fact]
    public void ResolvingSomewhereThatIsNotThereAnswersNothing()
    {
        var material = Material();

        // A field that does not exist, an index past the end, and a place that is not a pointer.
        Assert.Null(PointerPath.Resolve(material, "m_Colour"));
        Assert.Null(PointerPath.Resolve(material, "m_SavedProperties/m_TexEnvs/9/second/m_Texture"));
        Assert.Null(PointerPath.Resolve(material, "m_SavedProperties"));
        Assert.Null(PointerPath.Resolve(material, "m_Shader/m_PathID"));
    }

    [Fact]
    public void AResolvedPointerIsTheRealOne()
    {
        var material = Material();
        PointerPath.Resolve(material, "m_Shader")!["m_PathID"].AsLong = 42;

        Assert.Equal(42, material["m_Shader"]["m_PathID"].AsLong);
    }

    [Fact]
    public void APointerIsAFileAndAPathAndNothingElse()
    {
        Assert.True(PointerPath.IsPointer(Pointer("m_Shader", 0, 1)));

        // A pair of fields that happen to be two deep is not a pointer, and neither is a pointer
        // with something extra in it: mistaking one would rewrite a field that means something else.
        Assert.False(PointerPath.IsPointer(Node("m_Offset",
            Scalar("x", new AssetTypeValue(0f)), Scalar("y", new AssetTypeValue(0f)))));
        Assert.False(PointerPath.IsPointer(Node("odd",
            Scalar("m_FileID", new AssetTypeValue(0)),
            Scalar("m_PathID", new AssetTypeValue(0L)),
            Scalar("m_Extra", new AssetTypeValue(0)))));
    }
}
