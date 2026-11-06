using System.Buffers.Binary;

namespace OpenNV.Runtime.Content;

// TREE records name a SpeedTree 4 procedural .spt (generation parameters, not
// geometry). CNAM: leaf curvature, min/max leaf angle, branch/leaf dimming,
// shadow radius (int32; negative means 128), rock speed, rustle speed. BNAM:
// billboard width and height in game units. SNAM lists the shape seeds that
// placed references select by XSED index. ICON names the leaf composite atlas.
internal sealed record FalloutSpeedTree(
    FalloutFormKey Form,
    string Model,
    string Billboard,
    string LeafTexture,
    IReadOnlyList<int> Seeds,
    float BillboardWidth,
    float BillboardHeight,
    float LeafCurvature,
    float MinLeafAngle,
    float MaxLeafAngle,
    float BranchDimming,
    float LeafDimming,
    int ShadowRadius,
    float RockSpeed,
    float RustleSpeed)
{
    private const int DefaultShadowRadius = 128;
    private const byte UnassignedSeedIndex = 255;

    internal static FalloutSpeedTree Read(FalloutPluginRecord record, string billboardDirectory, string leafDirectory)
    {
        if (record.Signature != "TREE") throw new InvalidDataException("SpeedTree source requires TREE.");
        if (string.IsNullOrWhiteSpace(billboardDirectory) || string.IsNullOrWhiteSpace(leafDirectory))
            throw new ArgumentException("SpeedTree texture directories must not be empty.");
        var fields = record.ReadSubrecords().ToArray();
        var model = FalloutCellSceneReader.NormalizeModelPath(
            FalloutDialogueTopic.Text(Single(fields, "MODL").Span), "TREE");
        if (!model.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"TREE {record.FormKey} model {model} is not a SpeedTree .spt.");
        var parameters = Single(fields, "CNAM").Span;
        var dimensions = Single(fields, "BNAM").Span;
        if (parameters.Length != 32 || dimensions.Length != 8)
            throw new NotSupportedException($"TREE {record.FormKey} has unbound CNAM/BNAM sizes {parameters.Length}/{dimensions.Length}.");
        float At(ReadOnlySpan<byte> data, int index) => BinaryPrimitives.ReadSingleLittleEndian(data[(index * 4)..]);
        var width = At(dimensions, 0);
        var height = At(dimensions, 1);
        float[] values = [At(parameters, 0), At(parameters, 1), At(parameters, 2), At(parameters, 3), At(parameters, 4), At(parameters, 6), At(parameters, 7)];
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException($"TREE {record.FormKey} billboard dimensions are not positive.");
        if (values.Any(value => !float.IsFinite(value)) || values[5] < 0 || values[6] < 0)
            throw new InvalidDataException($"TREE {record.FormKey} wind/dimming parameters are invalid.");
        var shadowRadius = BinaryPrimitives.ReadInt32LittleEndian(parameters[20..]);
        // Each four-byte SNAM entry is one seed; zero entries are dropped at load.
        var seeds = new List<int>();
        foreach (var field in fields.Where(field => field.Signature == "SNAM"))
        {
            if (field.Data.Length % 4 != 0) throw new InvalidDataException($"TREE {record.FormKey} SNAM is not a list of int32 seeds.");
            for (var offset = 0; offset < field.Data.Length; offset += 4)
                if (BinaryPrimitives.ReadInt32LittleEndian(field.Data.Span[offset..]) is var seed and not 0) seeds.Add(seed);
        }
        var icon = FalloutDialogueTopic.Text(Single(fields, "ICON").Span).Replace('/', '\\').TrimStart('\\');
        if (icon.Length == 0 || icon.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(icon.Replace('\\', '/')))
            throw new InvalidDataException($"TREE {record.FormKey} leaf texture is not a relative texture name.");
        var name = Path.GetFileNameWithoutExtension(model.Replace('\\', '/'));
        return new(record.FormKey, model, Join(billboardDirectory, name + ".dds"), Join(leafDirectory, icon), seeds, width, height,
            values[0], values[1], values[2], Math.Clamp(values[3], 0, 1), Math.Clamp(values[4], 0, 1),
            shadowRadius < 0 ? DefaultShadowRadius : shadowRadius, values[5], values[6]);
    }

    // REFR XSED is one byte, or a legacy int32 truncated to its low byte. It
    // indexes SNAM modulo the list length. An unassigned index (255) or an
    // empty list falls back to the base form's own default: the SPT seed.
    internal int ReferenceSeed(byte? index, int sourceSeed) =>
        index is null or UnassignedSeedIndex || Seeds.Count == 0 ? sourceSeed : Seeds[index.Value % Seeds.Count];

    internal static byte? ReadSeedIndex(FalloutPluginRecord reference)
    {
        var fields = reference.ReadSubrecords().Where(field => field.Signature == "XSED").ToArray();
        return fields switch
        {
            [] => null,
            [{ Data.Length: 1 or 4 } field] => field.Data.Span[0],
            _ => throw new InvalidDataException($"Reference {reference.FormKey} XSED is not a single one- or four-byte seed index."),
        };
    }

    private static string Join(string directory, string file) => (directory.TrimEnd('\\', '/') + "\\" + file).ToLowerInvariant();

    private static ReadOnlyMemory<byte> Single(FalloutPluginSubrecord[] fields, string signature) =>
        fields.Where(field => field.Signature == signature).Select(field => field.Data).SingleOrDefault() is { Length: > 0 } data
            ? data : throw new InvalidDataException($"TREE has no single {signature}.");
}
