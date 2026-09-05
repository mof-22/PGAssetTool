using System.Text.Json;

namespace PGAssetTool.Core.Import.Meshes;

/// Reads a binary glTF far enough to get geometry out of it.
///
/// Only what a mesh needs: the JSON chunk, the binary chunk, and accessors resolved to plain arrays.
/// Sparse accessors and Draco compression are rejected rather than half-supported, since both change
/// what an accessor means and neither is produced by the exports this reads.
public sealed class GltfFile
{
    private readonly byte[] _binary;

    private GltfFile(JsonElement root, byte[] binary) => (Root, _binary) = (root, binary);

    public JsonElement Root { get; }

    public static GltfFile Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67)
            throw new InvalidDataException($"'{path}' is not a binary glTF.");

        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var root = JsonDocument.Parse(bytes.AsMemory(20, jsonLength)).RootElement;

        var binary = Array.Empty<byte>();
        var at = 20 + jsonLength;
        while (at + 8 <= bytes.Length)
        {
            var length = (int)BitConverter.ToUInt32(bytes, at);
            var kind = BitConverter.ToUInt32(bytes, at + 4);
            if (kind == 0x004E4942) binary = bytes[(at + 8)..(at + 8 + length)];
            at += 8 + length;
        }

        if (root.TryGetProperty("extensionsRequired", out var required))
            foreach (var extension in required.EnumerateArray())
                throw new NotSupportedException($"This file requires the glTF extension '{extension.GetString()}'.");

        return new GltfFile(root, binary);
    }

    public JsonElement? Find(string collection, int index)
    {
        if (!Root.TryGetProperty(collection, out var array) || index >= array.GetArrayLength()) return null;
        return array[index];
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new NotSupportedException($"Unknown component type {componentType}."),
    };

    private static int Components(string type) => type switch
    {
        "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
        _ => throw new NotSupportedException($"Unknown accessor type '{type}'."),
    };

    /// An accessor as floats, whatever it was stored as. Normalized integers are scaled the way the
    /// spec says; plain integers keep their value.
    public float[] ReadFloats(int accessorIndex, out int components)
    {
        var accessor = Find("accessors", accessorIndex)
            ?? throw new InvalidDataException($"No accessor {accessorIndex}.");
        if (accessor.TryGetProperty("sparse", out _))
            throw new NotSupportedException("Sparse accessors are not supported; turn that export option off.");

        var componentType = accessor.GetProperty("componentType").GetInt32();
        components = Components(accessor.GetProperty("type").GetString()!);
        var count = accessor.GetProperty("count").GetInt32();
        var normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();

        var values = new float[count * components];
        var size = ComponentSize(componentType);
        var stride = Stride(accessor, components * size, out var start);

        for (int e = 0; e < count; e++)
            for (int c = 0; c < components; c++)
            {
                var at = start + e * stride + c * size;
                values[e * components + c] = Read(at, componentType, normalized);
            }
        return values;
    }

    public int[] ReadIndices(int accessorIndex)
    {
        var floats = ReadFloats(accessorIndex, out _);
        var indices = new int[floats.Length];
        for (int i = 0; i < floats.Length; i++) indices[i] = (int)floats[i];
        return indices;
    }

    private int Stride(JsonElement accessor, int packed, out int start)
    {
        start = accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        if (!accessor.TryGetProperty("bufferView", out var viewIndex)) return packed;

        var view = Find("bufferViews", viewIndex.GetInt32())
            ?? throw new InvalidDataException("Accessor points at a missing bufferView.");
        start += view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
        return view.TryGetProperty("byteStride", out var s) ? s.GetInt32() : packed;
    }

    private float Read(int at, int componentType, bool normalized) => componentType switch
    {
        5120 => normalized ? Math.Max((sbyte)_binary[at] / 127f, -1f) : (sbyte)_binary[at],
        5121 => normalized ? _binary[at] / 255f : _binary[at],
        5122 => normalized ? Math.Max(BitConverter.ToInt16(_binary, at) / 32767f, -1f) : BitConverter.ToInt16(_binary, at),
        5123 => normalized ? BitConverter.ToUInt16(_binary, at) / 65535f : BitConverter.ToUInt16(_binary, at),
        5125 => BitConverter.ToUInt32(_binary, at),
        5126 => BitConverter.ToSingle(_binary, at),
        _ => 0f,
    };
}
