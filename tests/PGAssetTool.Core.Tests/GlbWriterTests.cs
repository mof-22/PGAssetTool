using System.Text.Json;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Tests;

public class GlbWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pgassettool-glb").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// One triangle, enough to check framing, handedness and winding without a game installed.
    private static UnityMesh Triangle(bool skinned = false) => new()
    {
        Name = "triangle",
        VertexCount = 3,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = [0, 0, 1, 1, 0, 2, 0, 1, 3],
            [VertexAttribute.Normal] = [0, 0, 1, 0, 0, 1, 0, 0, 1],
            [VertexAttribute.TexCoord0] = [0, 0, 1, 0, 0, 1],
            [VertexAttribute.BlendIndices] = skinned ? [0, 1, 0] : [],
        }.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.Normal] = 3,
            [VertexAttribute.TexCoord0] = 2,
            [VertexAttribute.BlendIndices] = 1,
        },
        Indices = [0, 1, 2],
        SubMeshes = [new SubMesh(0, 3, Topology: 0, BaseVertex: 0)],
        BindPoses = skinned ? [Identity(), Identity()] : [],
        BoneNameHashes = skinned ? [111u, 222u] : [],
    };

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private (JsonElement Json, byte[] Binary) WriteAndRead(UnityMesh mesh)
    {
        var path = Path.Combine(_directory, "out.glb");
        GlbWriter.Write(mesh, path);
        var bytes = File.ReadAllBytes(path);

        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));

        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength)).RootElement;
        var binaryStart = 20 + jsonLength + 8;
        return (json, bytes[binaryStart..]);
    }

    private static float[] Floats(JsonElement json, byte[] binary, int accessor, int components)
    {
        var a = json.GetProperty("accessors")[accessor];
        var view = json.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
        var count = a.GetProperty("count").GetInt32() * components;
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = BitConverter.ToSingle(binary, offset + i * 4);
        return values;
    }

    [Fact]
    public void TheContainerIsWellFormed()
    {
        var (json, binary) = WriteAndRead(Triangle());
        Assert.Equal("2.0", json.GetProperty("asset").GetProperty("version").GetString());
        Assert.Equal(binary.Length, json.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32()
            + (4 - json.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32() % 4) % 4);
    }

    [Fact]
    public void ZIsNegatedToTurnUnitysLeftHandedSpaceIntoGltfsRightHanded()
    {
        var (json, binary) = WriteAndRead(Triangle());
        var accessor = json.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("POSITION").GetInt32();

        // Source z values were 1, 2, 3.
        var positions = Floats(json, binary, accessor, 3);
        Assert.Equal([-1f, -2f, -3f], [positions[2], positions[5], positions[8]]);
    }

    [Fact]
    public void WindingIsReversedSoFacesKeepFacingOutward()
    {
        var (json, binary) = WriteAndRead(Triangle());
        var primitive = json.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var a = json.GetProperty("accessors")[primitive.GetProperty("indices").GetInt32()];
        var view = json.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        var offset = view.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;

        var indices = Enumerable.Range(0, 3).Select(i => BitConverter.ToUInt16(binary, offset + i * 2)).ToArray();
        Assert.Equal([(ushort)2, (ushort)1, (ushort)0], indices);
    }

    [Fact]
    public void TheTextureOriginIsMovedToTheTopLeft()
    {
        var (json, binary) = WriteAndRead(Triangle());
        var accessor = json.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32();

        // Source v values were 0, 0, 1.
        var uv = Floats(json, binary, accessor, 2);
        Assert.Equal([1f, 1f, 0f], [uv[1], uv[3], uv[5]]);
    }

    [Fact]
    public void PositionCarriesTheBoundsGltfRequires()
    {
        var (json, _) = WriteAndRead(Triangle());
        var accessor = json.GetProperty("accessors")[
            json.GetProperty("meshes")[0].GetProperty("primitives")[0]
                .GetProperty("attributes").GetProperty("POSITION").GetInt32()];

        Assert.Equal([0f, 0f, -3f], accessor.GetProperty("min").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal([1f, 1f, -1f], accessor.GetProperty("max").EnumerateArray().Select(v => v.GetSingle()));
    }

    [Fact]
    public void ASingleBoneBindGetsTheFourWidePairGltfExpects()
    {
        // Most meshes here bind one bone per vertex and carry no weight channel at all.
        var (json, binary) = WriteAndRead(Triangle(skinned: true));
        var attributes = json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");

        Assert.True(attributes.TryGetProperty("JOINTS_0", out _));
        var weights = Floats(json, binary, attributes.GetProperty("WEIGHTS_0").GetInt32(), 4);
        Assert.Equal([1f, 0f, 0f, 0f], weights[..4]);
        Assert.Equal(2, json.GetProperty("skins")[0].GetProperty("joints").GetArrayLength());
    }

    [Fact]
    public void AnUnskinnedMeshHasNoSkin()
    {
        var (json, _) = WriteAndRead(Triangle());
        Assert.False(json.TryGetProperty("skins", out _));
    }
}
