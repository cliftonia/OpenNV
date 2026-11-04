using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;

internal static partial class CellModelPathContracts
{
    // TREE CNAM/BNAM decode, billboard naming and the SPT container header.
    private static void RunSpeedTree(string directory)
    {
        var header = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(header, 1.34f);
        var parameters = new byte[32];
        float[] values = [2, 5, 85, .5f, .05f, 0, .07f, .35f];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(index * 4), values[index]);
        BinaryPrimitives.WriteInt32LittleEndian(parameters.AsSpan(20), 64);
        var dimensions = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(dimensions, 175); BinaryPrimitives.WriteSingleLittleEndian(dimensions.AsSpan(4), 160);
        var tree = Record("TREE", 0x400, 0, Field("MODL", Encoding.ASCII.GetBytes("\\WastelandShrub01.spt\0")),
            Field("CNAM", parameters), Field("BNAM", dimensions));
        var nif = Record("TREE", 0x401, 0, Field("MODL", Encoding.ASCII.GetBytes("Plants\\Tree.nif\0")),
            Field("CNAM", parameters), Field("BNAM", dimensions));
        File.WriteAllBytes(Path.Combine(directory, "Trees.esm"), [.. Record("TES4", 0, 0, Field("HEDR", header)), .. tree, .. nif]);
        using var stack = FalloutPluginStack.Load(directory, ["Trees.esm"]);
        var shrub = FalloutSpeedTree.Read(stack.GetEffective(new("Trees.esm", 0x400)), "textures\\trees\\billboards\\");
        if (shrub.Model != "trees\\wastelandshrub01.spt" || shrub.Billboard != "textures\\trees\\billboards\\wastelandshrub01.dds" ||
            shrub.BillboardWidth != 175 || shrub.BillboardHeight != 160 || shrub.LeafDimming != .05f ||
            shrub.RockSpeed != .07f || shrub.RustleSpeed != .35f)
            throw new InvalidDataException("TREE billboard dimensions, wind speeds or billboard path were not decoded from source.");
        try { _ = FalloutSpeedTree.Read(stack.GetEffective(new("Trees.esm", 0x401)), "textures\\trees\\billboards"); throw new InvalidOperationException(); }
        catch (NotSupportedException) { }

        var bark = "C:\\Trees\\Shrub\\\\ShrubBark.tga"u8.ToArray();
        var spt = new List<byte>();
        void Int(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); spt.AddRange(bytes); }
        Int(1000); Int(12); spt.AddRange("__IdvSpt_02_"u8.ToArray()); Int(1002); Int(2000); Int(bark.Length); spt.AddRange(bark);
        if (FalloutSpeedTree.ReadBarkTexture(spt.ToArray()) != "ShrubBark.tga")
            throw new InvalidDataException("SPT bark texture was not read from the general section.");
        var foreign = spt.ToArray(); foreign[10] = (byte)'X';
        try { _ = FalloutSpeedTree.ReadBarkTexture(foreign); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        try { _ = FalloutSpeedTree.ReadBarkTexture(spt.Take(spt.Count - 4).ToArray()); throw new InvalidOperationException(); }
        catch (InvalidDataException) { }
        Console.WriteLine("OPENNV_SPEEDTREE_CONTRACT_PASS cnam=true bnam=true billboard=true nifRefused=true sptHeader=true foreignRefused=true truncatedRefused=true");
    }
}
