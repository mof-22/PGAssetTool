using PGAssetTool.Core.Export.Meshes;
using PGAssetTool.Core.Import.Meshes;

namespace PGAssetTool.Core.Tests;

/// A mesh cannot choose its own bone list. Whatever SkinnedMeshRenderer draws it holds an array of
/// Transforms in the prefab, always the same length as the mesh's bind poses, and blend indices
/// point into that array by position — verified across several weapons in the game. Replacing a mesh
/// never touches the renderer, so the original bone list is the one that has to survive.
public class MeshImporterTests
{
    private static UnityMesh Mesh(int vertices, uint[] bones, float[]? blendIndices = null, int triangles = 1) => new()
    {
        Name = "mesh",
        VertexCount = vertices,
        Attributes = new Dictionary<VertexAttribute, float[]>
        {
            [VertexAttribute.Position] = new float[vertices * 3],
            [VertexAttribute.BlendIndices] = blendIndices ?? new float[vertices * 4],
            [VertexAttribute.BlendWeight] = Enumerable.Range(0, vertices * 4).Select(i => i % 4 == 0 ? 1f : 0f).ToArray(),
        },
        Dimensions = new Dictionary<VertexAttribute, int>
        {
            [VertexAttribute.Position] = 3,
            [VertexAttribute.BlendIndices] = 4,
            [VertexAttribute.BlendWeight] = 4,
        },
        Indices = Enumerable.Range(0, triangles * 3).Select(i => i % vertices).ToArray(),
        SubMeshes = [new SubMesh(0, triangles * 3, 0, 0)],
        BindPoses = bones.Select(_ => Identity()).ToList(),
        BoneNameHashes = bones,
    };

    /// A mesh exported with no armature: positions and nothing that mentions a bone.
    private static UnityMesh Unskinned(int vertices) => new()
    {
        Name = "ico_sphere",
        VertexCount = vertices,
        Attributes = new Dictionary<VertexAttribute, float[]> { [VertexAttribute.Position] = new float[vertices * 3] },
        Dimensions = new Dictionary<VertexAttribute, int> { [VertexAttribute.Position] = 3 },
        Indices = [0, 1, 2],
        SubMeshes = [new SubMesh(0, 3, 0, 0)],
        BindPoses = [],
        BoneNameHashes = [],
    };

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static (SkinStrategy Strategy, UnityMesh Mesh) Reconcile(UnityMesh before, UnityMesh replacement)
    {
        var method = typeof(MeshImporter).GetMethod("ReconcileSkin",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = method.Invoke(null, [before, replacement])!;
        var type = result.GetType();
        return ((SkinStrategy)type.GetField("Item1")!.GetValue(result)!,
                (UnityMesh)type.GetField("Item2")!.GetValue(result)!);
    }

    [Fact]
    public void TheSameBonesInTheSameOrderAreKept()
    {
        var before = Mesh(3, [10u, 20u, 30u]);
        Assert.Equal(SkinStrategy.Kept, Reconcile(before, Mesh(4, [10u, 20u, 30u])).Strategy);
    }

    [Fact]
    public void ReorderedBonesAreRemappedRatherThanTrusted()
    {
        // An exporter is free to reorder joints; the indices have to follow.
        var before = Mesh(3, [10u, 20u, 30u]);
        var replacement = Mesh(2, [30u, 10u, 20u], blendIndices: [2, 0, 0, 0, 1, 0, 0, 0]);

        var (strategy, result) = Reconcile(before, replacement);
        Assert.Equal(SkinStrategy.Remapped, strategy);

        // Joint 2 of the replacement is bone 20u, at index 1 in the original list; joint 1 is 10u, at 0.
        var indices = result.Get(VertexAttribute.BlendIndices)!;
        Assert.Equal(1f, indices[0]);
        Assert.Equal(0f, indices[4]);
    }

    [Fact]
    public void AForeignSkeletonIsBoundRigidlyInsteadOfWrittenThrough()
    {
        // A model brought in from elsewhere carries bones the renderer has never heard of. Writing
        // them would leave blend indices pointing at Transforms that are not in the prefab.
        var before = Mesh(3, [10u, 20u, 30u]);
        var replacement = Mesh(5, [777u, 888u], blendIndices: Enumerable.Repeat(1f, 20).ToArray());

        var (strategy, result) = Reconcile(before, replacement);
        Assert.Equal(SkinStrategy.BoundRigidly, strategy);
        AssertRigid(result);
    }

    [Fact]
    public void AModelWithNoSkinChannelsAtAllGetsThemBuilt()
    {
        // The worst case, and the common one: a plain mesh exported with no armature. It still has
        // to arrive with weights, or the renderer skins it with nothing.
        var before = Mesh(3, [10u, 20u]);
        var replacement = Unskinned(4);
        Assert.Null(replacement.Get(VertexAttribute.BlendWeight));

        var (strategy, result) = Reconcile(before, replacement);
        Assert.Equal(SkinStrategy.BoundRigidly, strategy);
        AssertRigid(result);
    }

    [Fact]
    public void AnUnskinnedOriginalHasNothingToReconcile()
    {
        Assert.Equal(SkinStrategy.None, Reconcile(Mesh(3, []), Mesh(9, [1u, 2u, 3u])).Strategy);
    }

    private static void AssertRigid(UnityMesh mesh)
    {
        var indices = mesh.Get(VertexAttribute.BlendIndices)!;
        var weights = mesh.Get(VertexAttribute.BlendWeight)!;
        Assert.Equal(mesh.VertexCount * 4, indices.Length);
        Assert.All(indices, i => Assert.Equal(0f, i));
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            Assert.Equal(1f, weights[v * 4]);
            Assert.Equal(0f, weights[v * 4 + 1]);
        }
    }
}
