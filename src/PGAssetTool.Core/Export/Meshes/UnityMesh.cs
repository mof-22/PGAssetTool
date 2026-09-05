using AssetsTools.NET;

namespace PGAssetTool.Core.Export.Meshes;

/// Unity's vertex attribute slots, in the order the channel array uses.
public enum VertexAttribute
{
    Position, Normal, Tangent, Color,
    TexCoord0, TexCoord1, TexCoord2, TexCoord3, TexCoord4, TexCoord5, TexCoord6, TexCoord7,
    BlendWeight, BlendIndices,
}

public enum VertexFormat
{
    Float32, Float16, UNorm8, SNorm8, UNorm16, SNorm16, UInt8, SInt8, UInt16, SInt16, UInt32, SInt32,
}

public sealed record SubMesh(int IndexStart, int IndexCount, int Topology, int BaseVertex);

/// A mesh decoded out of its packed vertex streams into plain float arrays.
///
/// Everything is widened to float (and joints to int) rather than kept in its source layout. The
/// packing is an engine detail — interleaved streams, per-attribute formats, alignment padding — and
/// carrying it further would only make every consumer re-implement the same unpacking.
public sealed class UnityMesh
{
    public required string Name { get; init; }
    public required int VertexCount { get; init; }
    public required IReadOnlyDictionary<VertexAttribute, float[]> Attributes { get; init; }
    public required IReadOnlyDictionary<VertexAttribute, int> Dimensions { get; init; }
    public required int[] Indices { get; init; }
    public required IReadOnlyList<SubMesh> SubMeshes { get; init; }
    public required IReadOnlyList<float[]> BindPoses { get; init; }
    public required IReadOnlyList<uint> BoneNameHashes { get; init; }

    public bool IsSkinned => BindPoses.Count > 0 && Attributes.ContainsKey(VertexAttribute.BlendIndices);

    public float[]? Get(VertexAttribute attribute) => Attributes.GetValueOrDefault(attribute);

    /// Byte payloads sit either on the field itself or on its Array child, depending on the type.
    private static byte[] Bytes(AssetTypeValueField field)
    {
        if (field.IsDummy) return [];
        if (field.Value?.ValueType == AssetValueType.ByteArray) return field.AsByteArray ?? [];
        var array = field["Array"];
        return !array.IsDummy && array.Value?.ValueType == AssetValueType.ByteArray
            ? array.AsByteArray ?? []
            : [];
    }

    public static UnityMesh Read(AssetTypeValueField field)
    {
        var name = field["m_Name"].AsString;
        var vertexData = field["m_VertexData"];
        var vertexCount = vertexData["m_VertexCount"].AsInt;
        var raw = Bytes(vertexData["m_DataSize"]);

        if (field["m_MeshCompression"].AsInt != 0)
            throw new NotSupportedException(
                $"'{name}' uses Unity's mesh compression, which packs vertices into a bit stream. "
                + "Nothing in this game does, so unpacking it is not implemented.");
        if (field["m_StreamData"]["path"].AsString.Length > 0)
            throw new NotSupportedException($"'{name}' keeps its vertex data outside the object.");

        var channels = ReadChannels(vertexData["m_Channels"]["Array"]);
        var (attributes, dimensions) = Unpack(channels, raw, vertexCount);

        return new UnityMesh
        {
            Name = name,
            VertexCount = vertexCount,
            Attributes = attributes,
            Dimensions = dimensions,
            Indices = ReadIndices(field),
            SubMeshes = ReadSubMeshes(field),
            BindPoses = field["m_BindPose"]["Array"].Children
                .Select(m => m.Children.SelectMany(r => r.Children.Select(v => v.AsFloat)).ToArray()).ToList(),
            BoneNameHashes = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToList(),
        };
    }

    private readonly record struct Channel(VertexAttribute Attribute, int Stream, int Offset, VertexFormat Format, int Dimension);

    private static List<Channel> ReadChannels(AssetTypeValueField array)
    {
        var channels = new List<Channel>();
        for (int i = 0; i < array.Children.Count && i <= (int)VertexAttribute.BlendIndices; i++)
        {
            var c = array.Children[i];
            var dimension = c["dimension"].AsInt & 0x0F;
            if (dimension == 0) continue;
            channels.Add(new Channel((VertexAttribute)i, c["stream"].AsInt, c["offset"].AsInt,
                (VertexFormat)c["format"].AsInt, dimension));
        }
        return channels;
    }

    private static int SizeOf(VertexFormat format) => format switch
    {
        VertexFormat.Float32 or VertexFormat.UInt32 or VertexFormat.SInt32 => 4,
        VertexFormat.Float16 or VertexFormat.UNorm16 or VertexFormat.SNorm16
            or VertexFormat.UInt16 or VertexFormat.SInt16 => 2,
        _ => 1,
    };

    /// Streams sit one after another in the buffer, each starting on a 16-byte boundary.
    private static (Dictionary<VertexAttribute, float[]>, Dictionary<VertexAttribute, int>) Unpack(
        List<Channel> channels, byte[] raw, int vertexCount)
    {
        var strides = channels.GroupBy(c => c.Stream)
            .ToDictionary(g => g.Key, g => g.Max(c => c.Offset + SizeOf(c.Format) * c.Dimension));

        var starts = new Dictionary<int, int>();
        var cursor = 0;
        foreach (var stream in strides.Keys.Order())
        {
            starts[stream] = cursor;
            cursor += Align(strides[stream] * vertexCount, 16);
        }

        var attributes = new Dictionary<VertexAttribute, float[]>();
        var dimensions = new Dictionary<VertexAttribute, int>();

        foreach (var channel in channels)
        {
            var values = new float[vertexCount * channel.Dimension];
            var stride = strides[channel.Stream];
            var start = starts[channel.Stream];

            for (int v = 0; v < vertexCount; v++)
            {
                var at = start + v * stride + channel.Offset;
                for (int c = 0; c < channel.Dimension; c++)
                    values[v * channel.Dimension + c] = ReadComponent(raw, at + c * SizeOf(channel.Format), channel.Format);
            }
            attributes[channel.Attribute] = values;
            dimensions[channel.Attribute] = channel.Dimension;
        }
        return (attributes, dimensions);
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    private static float ReadComponent(byte[] data, int at, VertexFormat format)
    {
        if (at < 0 || at + SizeOf(format) > data.Length) return 0f;
        return format switch
        {
            VertexFormat.Float32 => BitConverter.ToSingle(data, at),
            VertexFormat.Float16 => (float)BitConverter.ToHalf(data, at),
            VertexFormat.UNorm8 => data[at] / 255f,
            VertexFormat.SNorm8 => Math.Max((sbyte)data[at] / 127f, -1f),
            VertexFormat.UNorm16 => BitConverter.ToUInt16(data, at) / 65535f,
            VertexFormat.SNorm16 => Math.Max(BitConverter.ToInt16(data, at) / 32767f, -1f),
            VertexFormat.UInt8 => data[at],
            VertexFormat.SInt8 => (sbyte)data[at],
            VertexFormat.UInt16 => BitConverter.ToUInt16(data, at),
            VertexFormat.SInt16 => BitConverter.ToInt16(data, at),
            VertexFormat.UInt32 => BitConverter.ToUInt32(data, at),
            VertexFormat.SInt32 => BitConverter.ToInt32(data, at),
            _ => 0f,
        };
    }

    private static int[] ReadIndices(AssetTypeValueField field)
    {
        var raw = Bytes(field["m_IndexBuffer"]);
        // 0 means 16-bit indices, which is what every mesh here uses.
        if (field["m_IndexFormat"].AsInt == 0)
        {
            var indices = new int[raw.Length / 2];
            for (int i = 0; i < indices.Length; i++) indices[i] = BitConverter.ToUInt16(raw, i * 2);
            return indices;
        }
        var wide = new int[raw.Length / 4];
        for (int i = 0; i < wide.Length; i++) wide[i] = (int)BitConverter.ToUInt32(raw, i * 4);
        return wide;
    }

    private static List<SubMesh> ReadSubMeshes(AssetTypeValueField field)
    {
        var indexSize = field["m_IndexFormat"].AsInt == 0 ? 2 : 4;
        return field["m_SubMeshes"]["Array"].Children.Select(s => new SubMesh(
            IndexStart: (int)(s["firstByte"].AsUInt / indexSize),
            IndexCount: (int)s["indexCount"].AsUInt,
            Topology: s["topology"].AsInt,
            BaseVertex: s["baseVertex"].IsDummy ? 0 : (int)s["baseVertex"].AsUInt)).ToList();
    }
}
