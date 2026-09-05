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

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static SkinStrategy Reconcile(UnityMesh before, UnityMesh replacement)
    {
        var method = typeof(MeshImporter).GetMethod("ReconcileSkin",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (SkinStrategy)method.Invoke(null, [before, replacement])!;
    }

    [Fact]
    public void TheSameBonesInTheSameOrderAreKept()
    {
        var before = Mesh(3, [10u, 20u, 30u]);
        Assert.Equal(SkinStrategy.Kept, Reconcile(before, Mesh(4, [10u, 20u, 30u])));
    }

    [Fact]
    public void ReorderedBonesAreRemappedRatherThanTrusted()
    {
        // An exporter is free to reorder joints; the indices have to follow.
        var before = Mesh(3, [10u, 20u, 30u]);
        var replacement = Mesh(2, [30u, 10u, 20u], blendIndices: [2, 0, 0, 0, 1, 0, 0, 0]);

        Assert.Equal(SkinStrategy.Remapped, Reconcile(before, replacement));

        // Joint 2 of the replacement is bone 20u, at index 1 in the original list; joint 1 is 10u, at 0.
        var indices = replacement.Get(VertexAttribute.BlendIndices)!;
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

        Assert.Equal(SkinStrategy.BoundRigidly, Reconcile(before, replacement));
        Assert.All(replacement.Get(VertexAttribute.BlendIndices)!, i => Assert.Equal(0f, i));

        var weights = replacement.Get(VertexAttribute.BlendWeight)!;
        for (int v = 0; v < replacement.VertexCount; v++)
        {
            Assert.Equal(1f, weights[v * 4]);
            Assert.Equal(0f, weights[v * 4 + 1]);
        }
    }

    [Fact]
    public void AModelWithNoSkeletonAtAllIsAlsoBoundRigidly()
    {
        var before = Mesh(3, [10u, 20u]);
        var replacement = Mesh(4, []);
        Assert.Equal(SkinStrategy.BoundRigidly, Reconcile(before, replacement));
    }

    [Fact]
    public void AnUnskinnedOriginalHasNothingToReconcile()
    {
        Assert.Equal(SkinStrategy.None, Reconcile(Mesh(3, []), Mesh(9, [1u, 2u, 3u])));
    }
}
