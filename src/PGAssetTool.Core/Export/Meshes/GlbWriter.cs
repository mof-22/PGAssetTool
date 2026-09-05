using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PGAssetTool.Core.Export.Meshes;

/// Writes a mesh as a single-file binary glTF.
///
/// Unity is left-handed and glTF is right-handed, so Z is negated and triangle winding reversed —
/// flipping one axis reverses winding, and leaving it would turn every face inside out. Tangent
/// handedness flips with it.
public static class GlbWriter
{
    private const uint Magic = 0x46546C67;  // "glTF"
    private const uint Json = 0x4E4F534A;   // "JSON"
    private const uint Bin = 0x004E4942;    // "BIN"

    private const int Float = 5126;
    private const int UnsignedShort = 5123;
    private const int UnsignedInt = 5125;

    public static void Write(UnityMesh mesh, string path)
    {
        var buffer = new MemoryStream();
        var accessors = new JsonArray();
        var views = new JsonArray();

        var attributes = new JsonObject();
        foreach (var (attribute, name, size) in Mapping(mesh))
        {
            var values = Convert(mesh, attribute);
            if (values is null) continue;
            attributes[name] = AddAccessor(buffer, views, accessors, values, size, Float);
        }

        if (mesh.IsSkinned)
        {
            attributes["JOINTS_0"] = AddJoints(mesh, buffer, views, accessors);
            attributes["WEIGHTS_0"] = AddAccessor(buffer, views, accessors, Weights(mesh), 4, Float);
        }

        var primitives = new JsonArray();
        foreach (var submesh in mesh.SubMeshes.Where(s => s.Topology == 0 && s.IndexCount > 0))
        {
            var indices = new int[submesh.IndexCount];
            for (int i = 0; i < submesh.IndexCount; i += 3)
            {
                // Reversed to match the negated axis.
                indices[i] = mesh.Indices[submesh.IndexStart + i + 2] + submesh.BaseVertex;
                if (i + 1 < submesh.IndexCount) indices[i + 1] = mesh.Indices[submesh.IndexStart + i + 1] + submesh.BaseVertex;
                if (i + 2 < submesh.IndexCount) indices[i + 2] = mesh.Indices[submesh.IndexStart + i] + submesh.BaseVertex;
            }
            primitives.Add(new JsonObject
            {
                ["attributes"] = attributes.DeepClone(),
                ["indices"] = AddIndices(buffer, views, accessors, indices, mesh.VertexCount),
                ["mode"] = 4,
            });
        }

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "PGAssetTool" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = new JsonArray { 0 } } },
            ["nodes"] = new JsonArray { new JsonObject { ["mesh"] = 0, ["name"] = mesh.Name } },
            ["meshes"] = new JsonArray
            {
                new JsonObject { ["name"] = mesh.Name, ["primitives"] = primitives },
            },
            ["accessors"] = accessors,
            ["bufferViews"] = views,
        };

        if (mesh.IsSkinned) AddSkin(mesh, gltf, buffer, views, accessors);

        // Written last: the skin appends to the buffer, and a byteLength captured before that leaves
        // a bufferView pointing past the declared end, which readers are entitled to reject.
        var binary = buffer.ToArray();
        gltf["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } };

        WriteContainer(path, gltf, binary);
    }

    private static IEnumerable<(VertexAttribute, string, int)> Mapping(UnityMesh mesh)
    {
        yield return (VertexAttribute.Position, "POSITION", 3);
        yield return (VertexAttribute.Normal, "NORMAL", 3);
        yield return (VertexAttribute.Tangent, "TANGENT", 4);
        yield return (VertexAttribute.Color, "COLOR_0", 4);
        for (int i = 0; i < 8; i++)
            yield return (VertexAttribute.TexCoord0 + i, $"TEXCOORD_{i}", 2);
    }

    /// Widens to the size glTF expects and applies the handedness flip.
    private static float[]? Convert(UnityMesh mesh, VertexAttribute attribute)
    {
        var source = mesh.Get(attribute);
        if (source is null) return null;

        var dimension = mesh.Dimensions[attribute];
        var target = attribute switch
        {
            VertexAttribute.Position or VertexAttribute.Normal => 3,
            VertexAttribute.Tangent or VertexAttribute.Color => 4,
            _ => 2,
        };

        var result = new float[mesh.VertexCount * target];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            for (int c = 0; c < target; c++)
            {
                var value = c < dimension ? source[v * dimension + c] : c == 3 ? 1f : 0f;
                result[v * target + c] = value;
            }
            switch (attribute)
            {
                case VertexAttribute.Position or VertexAttribute.Normal:
                    result[v * target + 2] = -result[v * target + 2];
                    break;
                case VertexAttribute.Tangent:
                    result[v * target + 2] = -result[v * target + 2];
                    result[v * target + 3] = -result[v * target + 3];
                    break;
                case VertexAttribute.TexCoord0 or VertexAttribute.TexCoord1 or VertexAttribute.TexCoord2
                    or VertexAttribute.TexCoord3 or VertexAttribute.TexCoord4 or VertexAttribute.TexCoord5
                    or VertexAttribute.TexCoord6 or VertexAttribute.TexCoord7:
                    // glTF puts the texture origin at the top left; Unity puts it at the bottom.
                    result[v * target + 1] = 1f - result[v * target + 1];
                    break;
            }
        }
        return result;
    }

    /// Most meshes here bind one bone per vertex with no weight channel at all, which glTF still
    /// expects as a four-wide pair, so the remaining slots are filled with zero weight.
    private static float[] Weights(UnityMesh mesh)
    {
        var source = mesh.Get(VertexAttribute.BlendWeight);
        var dimension = source is null ? 0 : mesh.Dimensions[VertexAttribute.BlendWeight];
        var weights = new float[mesh.VertexCount * 4];

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (source is null || dimension == 0) { weights[v * 4] = 1f; continue; }
            float total = 0;
            for (int c = 0; c < 4; c++)
            {
                var value = c < dimension ? source[v * dimension + c] : 0f;
                weights[v * 4 + c] = value;
                total += value;
            }
            if (total <= 0) weights[v * 4] = 1f;
        }
        return weights;
    }

    private static JsonNode AddJoints(UnityMesh mesh, MemoryStream buffer, JsonArray views, JsonArray accessors)
    {
        var source = mesh.Get(VertexAttribute.BlendIndices)!;
        var dimension = mesh.Dimensions[VertexAttribute.BlendIndices];
        var joints = new ushort[mesh.VertexCount * 4];
        for (int v = 0; v < mesh.VertexCount; v++)
            for (int c = 0; c < 4; c++)
                joints[v * 4 + c] = c < dimension ? (ushort)Math.Max(source[v * dimension + c], 0) : (ushort)0;

        var bytes = new byte[joints.Length * 2];
        Buffer.BlockCopy(joints, 0, bytes, 0, bytes.Length);
        return AddRaw(buffer, views, accessors, bytes, joints.Length / 4, "VEC4", UnsignedShort);
    }

    private static void AddSkin(UnityMesh mesh, JsonObject gltf, MemoryStream buffer, JsonArray views, JsonArray accessors)
    {
        // Unity names these eRC — row R, column C — and reads out row by row. glTF wants them column
        // major, so the indices are transposed on the way out; leaving them as read produces
        // transposed matrices, which deform every vertex to somewhere it should not be.
        //
        // Negating Z means changing basis, not negating a fixed set of cells: with S = diag(1,1,-1,1)
        // the converted matrix is S·M·S, which flips exactly the elements where one of the row and
        // column is the Z index and not both.
        var matrices = new float[mesh.BindPoses.Count * 16];
        for (int b = 0; b < mesh.BindPoses.Count; b++)
        {
            var m = mesh.BindPoses[b];
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                {
                    var value = row * 4 + column < m.Length ? m[row * 4 + column] : 0f;
                    if (row == 2 ^ column == 2) value = -value;
                    matrices[b * 16 + column * 4 + row] = value;
                }
        }

        var nodes = (JsonArray)gltf["nodes"]!;
        var sceneNodes = (JsonArray)gltf["scenes"]![0]!["nodes"]!;
        var joints = new JsonArray();

        for (int i = 0; i < mesh.BindPoses.Count; i++)
        {
            // A joint left without a transform sits at the origin, and the deform at rest becomes the
            // inverse bind matrix applied to every vertex instead of cancelling against it — the mesh
            // is right in edit mode and wrong once the armature is on. Placing each joint at the
            // inverse of its inverse bind matrix is what makes the pair cancel.
            var placement = InvertAffine(matrices.AsSpan(i * 16, 16));

            joints.Add(nodes.Count);
            sceneNodes.Add(nodes.Count);
            nodes.Add(new JsonObject
            {
                ["name"] = i < mesh.BoneNameHashes.Count ? $"bone_{mesh.BoneNameHashes[i]}" : $"bone_{i}",
                ["matrix"] = new JsonArray(placement.Select(v => (JsonNode)v!).ToArray()),
            });
        }

        var accessor = AddAccessor(buffer, views, accessors, matrices, 16, Float, "MAT4");
        gltf["skins"] = new JsonArray
        {
            new JsonObject { ["joints"] = joints, ["inverseBindMatrices"] = accessor },
        };
        ((JsonObject)nodes[0]!)["skin"] = 0;
    }

    /// Inverts a column-major affine matrix: the linear part by cofactors, the translation by
    /// pushing it back through that inverse. Bind poses are rotations and translations, so a general
    /// 4x4 inverse would only add ways to be wrong.
    private static float[] InvertAffine(ReadOnlySpan<float> m)
    {
        // Column major, so m[column * 4 + row].
        float a = m[0], b = m[4], c = m[8];
        float d = m[1], e = m[5], f = m[9];
        float g = m[2], h = m[6], i = m[10];

        var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(determinant) < 1e-12f) return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        var s = 1f / determinant;

        var linear = new[]
        {
            (e * i - f * h) * s, (f * g - d * i) * s, (d * h - e * g) * s,
            (c * h - b * i) * s, (a * i - c * g) * s, (b * g - a * h) * s,
            (b * f - c * e) * s, (c * d - a * f) * s, (a * e - b * d) * s,
        };

        float tx = m[12], ty = m[13], tz = m[14];
        var result = new float[16];
        for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
                result[column * 4 + row] = linear[column * 3 + row];

        result[12] = -(linear[0] * tx + linear[3] * ty + linear[6] * tz);
        result[13] = -(linear[1] * tx + linear[4] * ty + linear[7] * tz);
        result[14] = -(linear[2] * tx + linear[5] * ty + linear[8] * tz);
        result[15] = 1f;
        return result;
    }

    private static JsonNode AddAccessor(
        MemoryStream buffer, JsonArray views, JsonArray accessors,
        float[] values, int components, int componentType, string? type = null)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        var kind = type ?? components switch { 1 => "SCALAR", 2 => "VEC2", 3 => "VEC3", 4 => "VEC4", _ => "MAT4" };
        var index = AddRaw(buffer, views, accessors, bytes, values.Length / components, kind, componentType);

        // POSITION is the one accessor glTF requires bounds on.
        if (kind == "VEC3" && components == 3)
        {
            var min = new float[3] { float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new float[3] { float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < values.Length; i += 3)
                for (int c = 0; c < 3; c++)
                {
                    min[c] = Math.Min(min[c], values[i + c]);
                    max[c] = Math.Max(max[c], values[i + c]);
                }
            var accessor = (JsonObject)accessors[(int)index!]!;
            accessor["min"] = new JsonArray(min.Select(v => (JsonNode)v!).ToArray());
            accessor["max"] = new JsonArray(max.Select(v => (JsonNode)v!).ToArray());
        }
        return index;
    }

    private static JsonNode AddIndices(
        MemoryStream buffer, JsonArray views, JsonArray accessors, int[] indices, int vertexCount)
    {
        if (vertexCount <= ushort.MaxValue)
        {
            var narrow = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++) narrow[i] = (ushort)indices[i];
            var bytes = new byte[narrow.Length * 2];
            Buffer.BlockCopy(narrow, 0, bytes, 0, bytes.Length);
            return AddRaw(buffer, views, accessors, bytes, indices.Length, "SCALAR", UnsignedShort);
        }
        var wide = new byte[indices.Length * 4];
        Buffer.BlockCopy(indices, 0, wide, 0, wide.Length);
        return AddRaw(buffer, views, accessors, wide, indices.Length, "SCALAR", UnsignedInt);
    }

    private static JsonNode AddRaw(
        MemoryStream buffer, JsonArray views, JsonArray accessors,
        byte[] bytes, int count, string type, int componentType)
    {
        while (buffer.Length % 4 != 0) buffer.WriteByte(0);
        var offset = buffer.Length;
        buffer.Write(bytes);

        views.Add(new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = bytes.Length,
        });
        accessors.Add(new JsonObject
        {
            ["bufferView"] = views.Count - 1,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        });
        return accessors.Count - 1;
    }

    private static void WriteContainer(string path, JsonObject gltf, byte[] binary)
    {
        var json = Encoding.UTF8.GetBytes(gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var jsonPadding = (4 - json.Length % 4) % 4;
        var binaryPadding = (4 - binary.Length % 4) % 4;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = new BinaryWriter(File.Create(path));

        output.Write(Magic);
        output.Write(2u);
        output.Write((uint)(12 + 8 + json.Length + jsonPadding + 8 + binary.Length + binaryPadding));

        output.Write((uint)(json.Length + jsonPadding));
        output.Write(Json);
        output.Write(json);
        for (int i = 0; i < jsonPadding; i++) output.Write((byte)0x20);

        output.Write((uint)(binary.Length + binaryPadding));
        output.Write(Bin);
        output.Write(binary);
        for (int i = 0; i < binaryPadding; i++) output.Write((byte)0);
    }
}
