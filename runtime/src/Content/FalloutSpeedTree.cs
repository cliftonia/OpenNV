using System.Buffers.Binary;
using System.Text;

namespace OpenNV.Runtime.Content;

// TREE records name a SpeedTreeCAD 4 procedural .spt (generation parameters,
// not geometry). The geometry generator is proprietary; the presentation draws
// the record's authored billboard, which retail itself uses beyond tree LOD.
// CNAM: leaf curvature, min/max leaf angle, branch/leaf dimming, shadow radius,
// rock speed, rustle speed. BNAM: billboard width and height in game units.
internal sealed record FalloutSpeedTree(
    FalloutFormKey Form,
    string Model,
    string Billboard,
    float BillboardWidth,
    float BillboardHeight,
    float LeafDimming,
    float RockSpeed,
    float RustleSpeed)
{
    private static readonly byte[] SptMagic = "__IdvSpt_02_"u8.ToArray();

    internal static FalloutSpeedTree Read(FalloutPluginRecord record, string billboardDirectory)
    {
        if (record.Signature != "TREE") throw new InvalidDataException("SpeedTree source requires TREE.");
        if (string.IsNullOrWhiteSpace(billboardDirectory))
            throw new ArgumentException("SpeedTree billboard directory must not be empty.", nameof(billboardDirectory));
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
        var leafDimming = At(parameters, 4);
        var rock = At(parameters, 6);
        var rustle = At(parameters, 7);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException($"TREE {record.FormKey} billboard dimensions are not positive.");
        if (!float.IsFinite(leafDimming) || !float.IsFinite(rock) || !float.IsFinite(rustle) || rock < 0 || rustle < 0)
            throw new InvalidDataException($"TREE {record.FormKey} wind/dimming parameters are invalid.");
        // The billboard shares the .spt base name, as the shipped
        // textures\trees\billboards set does for every FNV tree.
        var billboard = billboardDirectory.TrimEnd('\\', '/') + "\\" + Path.GetFileNameWithoutExtension(model.Replace('\\', '/')) + ".dds";
        return new(record.FormKey, model, billboard.ToLowerInvariant(), width, height, Math.Clamp(leafDimming, 0, 1), rock, rustle);
    }

    // Validates the procedural container header and returns its bark texture
    // name (token 2000), the first authored resource in every shipped tree.
    internal static string ReadBarkTexture(ReadOnlySpan<byte> spt)
    {
        if (spt.Length < 24 || BinaryPrimitives.ReadInt32LittleEndian(spt) != 1000 ||
            BinaryPrimitives.ReadInt32LittleEndian(spt[4..]) != SptMagic.Length || !spt.Slice(8, SptMagic.Length).SequenceEqual(SptMagic))
            throw new InvalidDataException("SpeedTree source is not an __IdvSpt_02_ container.");
        var offset = 8 + SptMagic.Length;
        if (BinaryPrimitives.ReadInt32LittleEndian(spt[offset..]) != 1002 || BinaryPrimitives.ReadInt32LittleEndian(spt[(offset + 4)..]) != 2000)
            throw new NotSupportedException("SpeedTree container does not open with its general/bark section.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(spt[(offset + 8)..]);
        if (length < 0 || offset + 12 + length > spt.Length)
            throw new InvalidDataException("SpeedTree bark texture name exceeds the container.");
        var path = Encoding.Latin1.GetString(spt.Slice(offset + 12, length));
        return path[(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1)..];
    }

    private static ReadOnlyMemory<byte> Single(FalloutPluginSubrecord[] fields, string signature) =>
        fields.Where(field => field.Signature == signature).Select(field => field.Data).SingleOrDefault() is { Length: > 0 } data
            ? data : throw new InvalidDataException($"TREE has no single {signature}.");
}
