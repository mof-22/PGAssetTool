using AssetsTools.NET;
using AssetsTools.NET.Extra;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export.Meshes;

namespace PGAssetTool.Core.Preview;

/// One object in a model's hierarchy: where it sits, whose child it is, and what it is called all
/// the way up.
///
/// <param name="Path">
/// The names from the top down, joined by slashes. A clip names what it drives by the same path,
/// counted from whichever object carries the Animation component — so matching is done on the end
/// of this rather than the whole of it, and nothing has to work out where that component is.
/// </param>
public sealed record Joint(string Name, string Path, int Parent, float[] Local);

/// The bones of a skinned mesh and everything between them, read out of the prefab.
///
/// A weapon in this game is one skinned mesh on a handful of bones — the slide, the magazine, the
/// hands — and its animations move those bones. So posing it needs no scene and no second mesh:
/// evaluate the clip, walk the hierarchy once, and the vertices follow.
public sealed class Skeleton
{
    private Skeleton(IReadOnlyList<Joint> joints, IReadOnlyList<int> bones, int root)
        => (Joints, Bones, Root) = (joints, bones, root);

    /// Every object between the root of the model and its bones, parents before children.
    public IReadOnlyList<Joint> Joints { get; }

    /// Which joint each of the mesh's bones is, in the order its bind poses and blend indices use.
    public IReadOnlyList<int> Bones { get; }

    /// The joint the renderer itself hangs off. Kept for what it says about the model rather than
    /// for posing, which works the space out from the model at rest instead.
    public int Root { get; }

    /// Where everything sits with nothing playing, worked out once: posing is relative to it, and
    /// it is the same for every frame of every clip.
    private float[][]? _rest;

    private float[][] Rest => _rest ??= World(null, 0);

    /// The skeleton a mesh is skinned to, or null for a mesh nothing skins.
    ///
    /// Found the way the dressing is: nothing points from a mesh to the renderer that draws it, so
    /// the renderers are read and the one naming this mesh answers. Its `m_Bones` is the order
    /// everything else is in.
    public static Skeleton? For(BundleSet bundles, string bundle, long meshPathId)
    {
        AssetsFileInstance file;
        try { file = bundles.Open(bundle); }
        catch (Exception e) when (e is IOException or FileNotFoundException) { return null; }

        foreach (var info in file.file.AssetInfos)
        {
            if (info.TypeId != (int)AssetClassID.SkinnedMeshRenderer) continue;

            var renderer = bundles.Context.Deserialize(file, info);
            if (renderer is null) continue;
            if (renderer["m_Mesh"]["m_PathID"].AsLong != meshPathId) continue;

            var bones = renderer["m_Bones"]["Array"].Children
                .Select(b => b["m_PathID"].AsLong)
                .Where(id => id != 0)
                .ToList();

            if (bones.Count == 0) return null;

            // The renderer's own transform, found through the GameObject it hangs off. The bind
            // poses are written against this one.
            var owner = renderer["m_GameObject"];
            var mine = owner.IsDummy ? 0 : TransformOf(bundles, file, owner["m_PathID"].AsLong);

            return Build(bundles, file, bones, mine);
        }

        return null;
    }

    private static long TransformOf(BundleSet bundles, AssetsFileInstance file, long gameObject)
    {
        var info = file.file.GetAssetInfo(gameObject);
        var field = info is null ? null : bundles.Context.Deserialize(file, info);
        if (field is null) return 0;

        foreach (var component in field["m_Component"]["Array"].Children)
        {
            var id = component["component"]["m_PathID"].AsLong;
            if (file.file.GetAssetInfo(id) is { TypeId: (int)AssetClassID.Transform }) return id;
        }

        return 0;
    }

    /// Walks up from each bone to the top, so every transform between them is present and a parent
    /// always comes before its children.
    private static Skeleton? Build(
        BundleSet bundles, AssetsFileInstance file, IReadOnlyList<long> bones, long renderer)
    {
        var joints = new List<Joint>();
        var at = new Dictionary<long, int>();

        int Add(long transform)
        {
            if (at.TryGetValue(transform, out var known)) return known;

            var info = file.file.GetAssetInfo(transform);
            var field = info is null ? null : bundles.Context.Deserialize(file, info);
            if (field is null) return -1;

            var father = field["m_Father"];
            var parent = father.IsDummy || father["m_PathID"].AsLong == 0
                ? -1
                : Add(father["m_PathID"].AsLong);

            // After the parent, always: everything downstream reads this list in order and expects
            // to have seen whoever it hangs off already.
            var owner = field["m_GameObject"];
            var named = owner.IsDummy ? null : file.file.GetAssetInfo(owner["m_PathID"].AsLong);
            var name = (named is null ? null : bundles.Context.Deserialize(file, named))?["m_Name"].AsString
                ?? "?";

            var path = parent >= 0 ? $"{joints[parent].Path}/{name}" : name;

            joints.Add(new Joint(name, path, parent, Trs(field)));
            return at[transform] = joints.Count - 1;
        }

        var order = bones.Select(Add).ToList();
        var root = renderer == 0 ? -1 : Add(renderer);

        return order.Any(i => i < 0) ? null : new Skeleton(joints, order, root);
    }

    private static float[] Trs(AssetTypeValueField transform)
    {
        var p = transform["m_LocalPosition"];
        var r = transform["m_LocalRotation"];
        var s = transform["m_LocalScale"];

        return
        [
            p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat,
            r["x"].AsFloat, r["y"].AsFloat, r["z"].AsFloat, r["w"].AsFloat,
            s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat,
        ];
    }

    /// Whether a clip has anything to say about this skeleton.
    ///
    /// A weapon's prefab carries clips for the whole of it — the flash, the shell casing — and only
    /// some of them move the gun. One that names nothing here would play as a model sitting still.
    public bool Moves(Motion motion)
        => motion.Curves.Any(c => Joints.Any(j => Matches(j.Path, c.Path)));

    /// A clip counts its paths from the object carrying the Animation component, which is somewhere
    /// below the root; the joints count theirs from the root. So one ends with the other.
    private static bool Matches(string joint, string curve)
        => joint.Equals(curve, StringComparison.Ordinal)
            || joint.EndsWith("/" + curve, StringComparison.Ordinal);

    /// Where every joint ends up, at one moment of one clip.
    ///
    /// Each joint's own place unless the clip says otherwise, then multiplied down the hierarchy —
    /// which is the whole of what an animation does to a model of this kind.
    public float[][] World(Motion? motion, float time)
    {
        var pose = motion?.Pose(time);
        var world = new float[Joints.Count][];

        for (var at = 0; at < Joints.Count; at++)
        {
            var joint = Joints[at];
            var local = joint.Local;

            if (pose is not null)
                foreach (var (path, placed) in pose)
                {
                    if (!Matches(joint.Path, path)) continue;
                    local = Moved(local, placed);
                    break;
                }

            var matrix = Matrix.Compose(local);
            world[at] = joint.Parent >= 0 ? Matrix.Times(world[joint.Parent], matrix) : matrix;
        }

        return world;
    }

    private static float[] Moved(float[] local, Placed placed)
    {
        var moved = (float[])local.Clone();

        if (placed is { HasPosition: true, Position: { } p }) (moved[0], moved[1], moved[2]) = (p[0], p[1], p[2]);
        if (placed is { HasRotation: true, Rotation: { } r })
            (moved[3], moved[4], moved[5], moved[6]) = (r[0], r[1], r[2], r[3]);
        if (placed is { HasScale: true, Scale: { } s }) (moved[7], moved[8], moved[9]) = (s[0], s[1], s[2]);

        return moved;
    }

    /// The mesh with its vertices moved to where this clip puts them at this moment.
    ///
    /// Ordinary linear blend skinning: every vertex is carried by up to four bones, each with a
    /// weight, and ends up at the weighted sum of where each of them would put it. The bind pose is
    /// what takes a vertex out of the model's own space and into the bone's, which is why it is
    /// stored with the mesh rather than with the skeleton.
    public UnityMesh Pose(UnityMesh mesh, Motion? motion, float time)
    {
        if (mesh.Get(VertexAttribute.Position) is not { } positions) return mesh;
        if (mesh.Get(VertexAttribute.BlendIndices) is not { } joints) return mesh;
        if (mesh.BindPoses.Count == 0) return mesh;

        // A mesh carried by one bone per vertex has no weights at all — there is nothing to weigh
        // against — and that is how this game's weapons are rigged: the slide belongs to the slide's
        // bone and to nothing else. Reading the missing channel as "no weight anywhere" left every
        // vertex where it started, which looks exactly like an animation that does nothing.
        var weights = mesh.Get(VertexAttribute.BlendWeight);
        var wide = mesh.Dimensions.GetValueOrDefault(VertexAttribute.BlendIndices, 1);

        var world = World(motion, time);

        // Back into the space the mesh's own vertices are written in.
        //
        // A bind pose is written against whatever the model was parented to when it was rigged, and
        // that is not something the prefab still says: the beretta's bind poses put its bones at the
        // origin, where its prefab hangs them a metre and a half up and to the right. So the space
        // is taken from the model itself — at rest, every bone's skinning matrix is the same one,
        // and its inverse is by construction the transform that leaves the mesh where it was read.
        var home = Matrix.Inverse(Matrix.Times(
            Rest[Bones[0]], Matrix.FromRows(mesh.BindPoses[0])));

        // One matrix per bone: where the bone is now, applied after taking the vertex into its space.
        var skinning = new float[Bones.Count][];
        for (var bone = 0; bone < Bones.Count; bone++)
            skinning[bone] = bone < mesh.BindPoses.Count && Bones[bone] < world.Length
                ? Matrix.Times(home,
                    Matrix.Times(world[Bones[bone]], Matrix.FromRows(mesh.BindPoses[bone])))
                : Matrix.Identity;

        var moved = new float[positions.Length];
        var normals = mesh.Get(VertexAttribute.Normal);
        var turned = normals is null ? null : new float[normals.Length];

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            float x = 0, y = 0, z = 0, nx = 0, ny = 0, nz = 0, total = 0;

            for (var slot = 0; slot < wide; slot++)
            {
                var at = vertex * wide + slot;
                if (at >= joints.Length) break;

                var weight = weights is null
                    ? slot == 0 ? 1f : 0f
                    : at < weights.Length ? weights[at] : 0f;

                if (weight <= 0) continue;

                var bone = (int)joints[at];
                if (bone < 0 || bone >= skinning.Length) continue;

                var m = skinning[bone];
                var (px, py, pz) = (positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);

                x += weight * (m[0] * px + m[4] * py + m[8] * pz + m[12]);
                y += weight * (m[1] * px + m[5] * py + m[9] * pz + m[13]);
                z += weight * (m[2] * px + m[6] * py + m[10] * pz + m[14]);

                if (normals is not null)
                {
                    var (ax, ay, az) = (normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]);
                    nx += weight * (m[0] * ax + m[4] * ay + m[8] * az);
                    ny += weight * (m[1] * ax + m[5] * ay + m[9] * az);
                    nz += weight * (m[2] * ax + m[6] * ay + m[10] * az);
                }

                total += weight;
            }

            // A vertex no bone carries stays where the model has it rather than collapsing to the
            // origin, which is what a weight of zero would otherwise do to it.
            if (total <= 0)
            {
                Array.Copy(positions, vertex * 3, moved, vertex * 3, 3);
                if (normals is not null && turned is not null)
                    Array.Copy(normals, vertex * 3, turned, vertex * 3, 3);
                continue;
            }

            (moved[vertex * 3], moved[vertex * 3 + 1], moved[vertex * 3 + 2]) = (x, y, z);

            if (turned is not null)
            {
                var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length > 1e-6f) (nx, ny, nz) = (nx / length, ny / length, nz / length);
                (turned[vertex * 3], turned[vertex * 3 + 1], turned[vertex * 3 + 2]) = (nx, ny, nz);
            }
        }

        var attributes = new Dictionary<VertexAttribute, float[]>(mesh.Attributes)
        {
            [VertexAttribute.Position] = moved,
        };
        if (turned is not null) attributes[VertexAttribute.Normal] = turned;

        return mesh with { Attributes = attributes };
    }
}

/// Column-major 4x4s, the way Unity writes a bind pose, so the two can be multiplied without
/// transposing anything on the way past.
public static class Matrix
{
    public static float[] Identity => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// A local transform — position, rotation as a quaternion, scale — as one matrix.
    public static float[] Compose(float[] trs)
    {
        float x = trs[3], y = trs[4], z = trs[5], w = trs[6];
        float sx = trs[7], sy = trs[8], sz = trs[9];

        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, xz = x * z, yz = y * z;
        float wx = w * x, wy = w * y, wz = w * z;

        return
        [
            (1 - 2 * (yy + zz)) * sx, (2 * (xy + wz)) * sx, (2 * (xz - wy)) * sx, 0,
            (2 * (xy - wz)) * sy, (1 - 2 * (xx + zz)) * sy, (2 * (yz + wx)) * sy, 0,
            (2 * (xz + wy)) * sz, (2 * (yz - wx)) * sz, (1 - 2 * (xx + yy)) * sz, 0,
            trs[0], trs[1], trs[2], 1,
        ];
    }

    /// A bind pose as the mesh keeps it, which is the other way round.
    ///
    /// Unity writes a Matrix4x4 as sixteen fields named e00 through e33 — row then column — and the
    /// mesh reader keeps that order. Everything here is column-major, so one of the two has to turn.
    public static float[] FromRows(float[] rows)
    {
        var columns = new float[16];

        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 4; column++)
            columns[column * 4 + row] = row * 4 + column < rows.Length ? rows[row * 4 + column] : 0f;

        return columns;
    }

    /// The inverse of a transform, which is all these ever are: a rotation and scale with a
    /// translation, and no perspective in the last row to worry about.
    public static float[] Inverse(float[] m)
    {
        // The determinant of the upper 3x3. A model with a flattened axis has none, and an identity
        // is the only answer that cannot make things worse.
        var determinant =
            m[0] * (m[5] * m[10] - m[9] * m[6])
            - m[4] * (m[1] * m[10] - m[9] * m[2])
            + m[8] * (m[1] * m[6] - m[5] * m[2]);

        if (MathF.Abs(determinant) < 1e-12f) return Identity;
        var scale = 1f / determinant;

        var inverse = new float[16];

        inverse[0] = (m[5] * m[10] - m[9] * m[6]) * scale;
        inverse[1] = (m[9] * m[2] - m[1] * m[10]) * scale;
        inverse[2] = (m[1] * m[6] - m[5] * m[2]) * scale;
        inverse[4] = (m[8] * m[6] - m[4] * m[10]) * scale;
        inverse[5] = (m[0] * m[10] - m[8] * m[2]) * scale;
        inverse[6] = (m[4] * m[2] - m[0] * m[6]) * scale;
        inverse[8] = (m[4] * m[9] - m[8] * m[5]) * scale;
        inverse[9] = (m[8] * m[1] - m[0] * m[9]) * scale;
        inverse[10] = (m[0] * m[5] - m[4] * m[1]) * scale;

        inverse[12] = -(inverse[0] * m[12] + inverse[4] * m[13] + inverse[8] * m[14]);
        inverse[13] = -(inverse[1] * m[12] + inverse[5] * m[13] + inverse[9] * m[14]);
        inverse[14] = -(inverse[2] * m[12] + inverse[6] * m[13] + inverse[10] * m[14]);
        inverse[15] = 1;

        return inverse;
    }

    public static float[] Times(float[] a, float[] b)
    {
        var result = new float[16];

        for (var column = 0; column < 4; column++)
        for (var row = 0; row < 4; row++)
            result[column * 4 + row] =
                a[row] * b[column * 4]
                + a[4 + row] * b[column * 4 + 1]
                + a[8 + row] * b[column * 4 + 2]
                + a[12 + row] * b[column * 4 + 3];

        return result;
    }
}
