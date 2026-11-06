using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Presentation.Rendering;

// Copies generated SpeedTree geometry (game units, +Z up) into one ArrayMesh
// with a bark surface and a leaf-card surface. CUSTOM0 carries per-vertex
// tree data as floats: (shade or lightness, wind weight, wind matrix, rock
// phase z/48). Leaf cards keep their four vertices at the leaf centre; UV2
// holds the corner offset in metres for the camera-facing vertex program.
internal static class RuntimeNativeSpeedTreeMesh
{
    private const float LeafTableEntries = 48;
    private const Mesh.ArrayFormat FloatCustom0 = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);

    internal static ArrayMesh Build(FalloutSpeedTreeGeometry geometry, float units, Material branch, Material leaf)
    {
        var mesh = new ArrayMesh();
        Vector3 Point(System.Numerics.Vector3 value) => new Vector3(value.X, value.Z, -value.Y) * units;
        Vector3 Direction(System.Numerics.Vector3 value) => new(value.X, value.Z, -value.Y);
        if (geometry.BranchIndices.Count > 0)
        {
            var vertices = geometry.BranchVertices;
            Add(mesh, branch, vertices.Select(vertex => Point(vertex.Position)).ToArray(), vertices.Select(vertex => Direction(vertex.Normal)).ToArray(),
                vertices.Select(vertex => new Vector2(vertex.Uv.X, vertex.Uv.Y)).ToArray(), null,
                vertices.SelectMany(vertex => new[] { vertex.Shade, vertex.Wind, vertex.WindMatrix, 0f }).ToArray(), geometry.BranchIndices.ToArray());
        }
        if (geometry.Leaves.Count > 0)
        {
            var count = geometry.Leaves.Count * 4;
            var points = new Vector3[count]; var normals = new Vector3[count];
            var uvs = new Vector2[count]; var corners = new Vector2[count];
            var custom = new float[count * 4];
            var indices = new int[geometry.Leaves.Count * 6];
            for (var index = 0; index < geometry.Leaves.Count; index++)
            {
                var card = geometry.Leaves[index];
                for (var corner = 0; corner < 4; corner++)
                {
                    var at = index * 4 + corner;
                    points[at] = Point(card.Position);
                    normals[at] = Direction(card.Normal);
                    uvs[at] = new(card.Uvs[corner].X, card.Uvs[corner].Y);
                    corners[at] = new Vector2(card.Corners[corner].X, card.Corners[corner].Y) * units;
                    var table = card.Group + (corner + 2) % 4 + card.Lightness;
                    custom[at * 4] = card.Lightness; custom[at * 4 + 1] = card.Wind;
                    custom[at * 4 + 2] = card.WindMatrix; custom[at * 4 + 3] = table / LeafTableEntries;
                }
                var first = index * 4;
                int[] triangle = [3, 1, 2, 0, 1, 3];
                for (var vertex = 0; vertex < 6; vertex++) indices[index * 6 + vertex] = first + triangle[vertex];
            }
            Add(mesh, leaf, points, normals, uvs, corners, custom, indices);
        }
        return mesh;
    }

    // Local bounds of the generated tree in metres: branch vertices and leaf
    // centres widened by each card's largest corner reach.
    internal static Aabb Bounds(FalloutSpeedTreeGeometry geometry, float units)
    {
        Vector3 Point(System.Numerics.Vector3 value) => new Vector3(value.X, value.Z, -value.Y) * units;
        Aabb? bounds = null;
        void Include(Aabb next) => bounds = bounds is { } prior ? prior.Merge(next) : next;
        foreach (var vertex in geometry.BranchVertices) Include(new Aabb(Point(vertex.Position), Vector3.Zero));
        foreach (var leaf in geometry.Leaves)
        {
            var reach = leaf.Corners.Max(corner => corner.Length()) * units;
            Include(new Aabb(Point(leaf.Position) - Vector3.One * reach, Vector3.One * (2 * reach)));
        }
        return bounds ?? throw new InvalidDataException("Generated SpeedTree geometry is empty.");
    }

    private static void Add(ArrayMesh mesh, Material material, Vector3[] points, Vector3[] normals, Vector2[] uvs, Vector2[]? corners,
        float[] custom, int[] indices)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        if (corners is not null) arrays[(int)Mesh.ArrayType.TexUV2] = corners;
        arrays[(int)Mesh.ArrayType.Custom0] = custom;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: FloatCustom0);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }
}
