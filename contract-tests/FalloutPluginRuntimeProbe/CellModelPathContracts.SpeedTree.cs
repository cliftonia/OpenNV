using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;

internal static partial class CellModelPathContracts
{
    // TREE CNAM/BNAM/SNAM/ICON decode, XSED seed selection, the SPT token
    // stream, the shared random generator, splines and generation determinism.
    private static void RunSpeedTree(string directory)
    {
        var header = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(header, 1.34f);
        var parameters = new byte[32];
        float[] values = [2, 5, 85, .5f, .05f, 0, .07f, .35f];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(index * 4), values[index]);
        BinaryPrimitives.WriteInt32LittleEndian(parameters.AsSpan(20), 64);
        var unshadowed = parameters.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(unshadowed.AsSpan(20), -1);
        var dimensions = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(dimensions, 175); BinaryPrimitives.WriteSingleLittleEndian(dimensions.AsSpan(4), 160);
        var seeds = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(seeds, 301409); BinaryPrimitives.WriteInt32LittleEndian(seeds.AsSpan(8), 554776);
        byte[] Icon(string value) => Encoding.ASCII.GetBytes(value + "\0");
        var tree = Record("TREE", 0x400, 0, Field("MODL", Encoding.ASCII.GetBytes("\\WastelandShrub01.spt\0")),
            Field("ICON", Icon("WastelandShrub01Foliage.dds")), Field("SNAM", seeds), Field("CNAM", parameters), Field("BNAM", dimensions));
        var nif = Record("TREE", 0x401, 0, Field("MODL", Encoding.ASCII.GetBytes("Plants\\Tree.nif\0")),
            Field("ICON", Icon("Leaves.dds")), Field("CNAM", parameters), Field("BNAM", dimensions));
        var bare = Record("TREE", 0x402, 0, Field("MODL", Encoding.ASCII.GetBytes("Bush.spt\0")),
            Field("ICON", Icon("Bush.dds")), Field("CNAM", unshadowed), Field("BNAM", dimensions));
        var escaping = Record("TREE", 0x403, 0, Field("MODL", Encoding.ASCII.GetBytes("Bush.spt\0")),
            Field("ICON", Icon("..\\Bush.dds")), Field("CNAM", parameters), Field("BNAM", dimensions));
        var placed = Record("REFR", 0x500, 0, Field("NAME", BitConverter.GetBytes(0x400u)), Field("XSED", [4]));
        var legacy = Record("REFR", 0x501, 0, Field("NAME", BitConverter.GetBytes(0x400u)), Field("XSED", BitConverter.GetBytes(0x101)));
        var unassigned = Record("REFR", 0x502, 0, Field("NAME", BitConverter.GetBytes(0x400u)));
        var malformed = Record("REFR", 0x503, 0, Field("NAME", BitConverter.GetBytes(0x400u)), Field("XSED", [1, 2]));
        File.WriteAllBytes(Path.Combine(directory, "Trees.esm"),
            [.. Record("TES4", 0, 0, Field("HEDR", header)), .. tree, .. nif, .. bare, .. escaping, .. placed, .. legacy, .. unassigned, .. malformed]);
        using var stack = FalloutPluginStack.Load(directory, ["Trees.esm"]);
        FalloutSpeedTree Read(uint id) => FalloutSpeedTree.Read(stack.GetEffective(new("Trees.esm", id)), "textures\\trees\\billboards\\", "Textures\\Trees\\Leaves");
        var shrub = Read(0x400);
        Require(shrub.Model == "trees\\wastelandshrub01.spt" && shrub.Billboard == "textures\\trees\\billboards\\wastelandshrub01.dds" &&
            shrub.LeafTexture == "textures\\trees\\leaves\\wastelandshrub01foliage.dds" && shrub.BillboardWidth == 175 && shrub.BillboardHeight == 160 &&
            shrub.LeafCurvature == 2 && shrub.MinLeafAngle == 5 && shrub.MaxLeafAngle == 85 && shrub.BranchDimming == .5f &&
            shrub.LeafDimming == .05f && shrub.ShadowRadius == 64 && shrub.RockSpeed == .07f && shrub.RustleSpeed == .35f,
            "TREE CNAM, BNAM, ICON or billboard path was not decoded from source.");
        Require(shrub.Seeds.SequenceEqual([301409, 554776]), "TREE SNAM seed list kept a zero entry or lost a seed.");
        Require(Read(0x402).ShadowRadius == 128 && Read(0x402).Seeds.Count == 0, "A negative TREE shadow radius did not become 128.");
        Reject<NotSupportedException>(() => Read(0x401));
        Reject<InvalidDataException>(() => Read(0x403));
        byte? Index(uint id) => FalloutSpeedTree.ReadSeedIndex(stack.GetEffective(new("Trees.esm", id)));
        Require(Index(0x500) == 4 && Index(0x501) == 1 && Index(0x502) is null, "REFR XSED seed index was not read as a byte.");
        Reject<InvalidDataException>(() => Index(0x503));
        Require(shrub.ReferenceSeed(4, 7) == 301409 && shrub.ReferenceSeed(1, 7) == 554776 && shrub.ReferenceSeed(255, 7) == 7 &&
            shrub.ReferenceSeed(null, 7) == 7 && Read(0x402).ReferenceSeed(3, 9) == 9, "Reference seed did not index SNAM modulo its length.");

        // The shared generator is deterministic per seed, uniform in range,
        // and every spline evaluation consumes a draw even with no variance.
        var first = new FalloutSpeedTreeRandom(301409); var second = new FalloutSpeedTreeRandom(301409);
        var drawn = Enumerable.Range(0, 256).Select(_ => first.Uniform(0, 1)).ToArray();
        Require(drawn.SequenceEqual(Enumerable.Range(0, 256).Select(_ => second.Uniform(0, 1))) && drawn.All(value => value is >= 0 and < 1) &&
            drawn.Distinct().Count() > 250, "SpeedTree random generator is not a deterministic uniform stream.");
        var ramp = FalloutSpeedTreeSpline.Parse("BezierSpline 2\t4\t0\n{\n\n\t2\n\t0 0 0.707107 0.707107 0.1\n\t1 1 0.707107 0.707107 0.1\n\n}\n");
        var steady = new FalloutSpeedTreeRandom(5); var consumed = new FalloutSpeedTreeRandom(5);
        Require(Math.Abs(ramp.Evaluate(0, steady) - 2) < 1e-4f && Math.Abs(ramp.Evaluate(1, steady) - 4) < 1e-4f &&
            Math.Abs(ramp.Evaluate(.5f, steady) - 3) < .02f, "SpeedTree spline lookup did not map x through min/max.");
        _ = consumed.Uniform(0, 1); _ = consumed.Uniform(0, 1); _ = consumed.Uniform(0, 1);
        Require(steady.Uniform(0, 1) == consumed.Uniform(0, 1), "A zero-variance spline evaluation did not consume one random draw.");
        Reject<InvalidDataException>(() => FalloutSpeedTreeSpline.Parse("BezierSpline 0 1 0 { 3 0 0 1 1 0.1 }"));
        Reject<InvalidDataException>(() => FalloutSpeedTreeSpline.Parse("BezierSpline 0 1 0 { 858993461 0 0 1 1 0.1 1 1 1 1 0.1 }"));
        Reject<InvalidDataException>(() => FalloutSpeedTreeSpline.Parse("BezierSpline 0 1 0 { 2.5 0 0 1 1 0.1 1 1 1 1 0.1 }"));

        var spt = SyntheticSpt();
        var source = FalloutSpeedTreeSource.Read(spt);
        Require(source.BarkTexture == "ShrubBark.tga" && source.LodFar == 600 && source.LodNear == 100 && source.Seed == 99 &&
            source.Size == 200 && source.SizeVariance == 10 && source.Levels.Count == 3 && source.Levels[0].Sides == 8 &&
            source.Levels[1].Sides == 6 && source.Levels[1].Segments == 3 && source.Levels[0].Frequency == 50 &&
            source.LeafMaps.Count == 1 && source.LeafMaps[0].Texture == "Leaf.tga" && source.LeafMaps[0].Corners[2] == new System.Numerics.Vector2(0, 1),
            "SPT general, level, leaf-map or composite-coordinate tokens were not decoded.");
        var malformedToken = spt.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformedToken.AsSpan(spt.AsSpan().IndexOf(BitConverter.GetBytes(2001))), 7777);
        Reject<InvalidDataException>(() => FalloutSpeedTreeSource.Read(malformedToken));
        Reject<InvalidDataException>(() => FalloutSpeedTreeSource.Read(spt[..^3]));
        var foreign = spt.ToArray(); foreign[10] = (byte)'X';
        Reject<InvalidDataException>(() => FalloutSpeedTreeSource.Read(foreign));

        // Oversized or unbounded sources fail closed instead of exhausting memory.
        var flood = FalloutSpeedTreeSource.Read(SyntheticSpt(branchFrequency: 1e9f));
        Reject<InvalidDataException>(() => FalloutSpeedTreeGenerator.Generate(flood, 1, 1, .5f));
        var dense = FalloutSpeedTreeSource.Read(SyntheticSpt(branchFrequency: 4000, leafFrequency: 20000));
        Reject<InvalidDataException>(() => FalloutSpeedTreeGenerator.Generate(dense, 1, 1, .5f));
        Reject<NotSupportedException>(() => FalloutSpeedTreeSource.Read(SyntheticSpt(extraLevels: 7)));
        Reject<InvalidDataException>(() => FalloutSpeedTreeSource.Read(SyntheticSpt(leafFrequency: float.NaN)));
        var tree0 = FalloutSpeedTreeGenerator.Generate(source, 301409, 1, .5f);
        var tree1 = FalloutSpeedTreeGenerator.Generate(source, 301409, 1, .5f);
        var tree2 = FalloutSpeedTreeGenerator.Generate(source, 554776, 1, .5f);
        Require(tree0.BranchVertices.SequenceEqual(tree1.BranchVertices) && tree0.BranchIndices.SequenceEqual(tree1.BranchIndices) &&
            tree0.Leaves.Select(leaf => leaf.Position).SequenceEqual(tree1.Leaves.Select(leaf => leaf.Position)),
            "SpeedTree generation is not deterministic for one seed.");
        Require(!tree0.BranchVertices.Select(vertex => vertex.Position).SequenceEqual(tree2.BranchVertices.Select(vertex => vertex.Position)),
            "Different SpeedTree seeds produced one shape.");
        var trunkTop = tree0.BranchVertices.Take(9 * 9).Max(vertex => vertex.Position.Z);
        Require(tree0.Leaves.Count > 0 && tree0.BranchIndices.Count % 3 == 0 && tree0.BranchIndices.All(index => index < tree0.BranchVertices.Count) &&
            trunkTop > tree0.Size * .3f && tree0.BranchVertices.All(vertex => float.IsFinite(vertex.Position.X + vertex.Position.Y + vertex.Position.Z) &&
                vertex.WindMatrix is >= 0 and < 4 && vertex.Shade is > 0 and <= 1) &&
            tree0.Leaves.All(leaf => leaf.Lightness is > 0 and < 1 && leaf.Group is >= 0 and < 48 && leaf.Corners.Count == 4),
            "Generated SpeedTree trunk, branch strips or leaf cards are malformed.");
        Console.WriteLine("OPENNV_SPEEDTREE_CONTRACT_PASS cnam=true snamSeeds=true xsedIndex=true icon=true shadowRadiusInt=true nifRefused=true " +
            $"rng=true splineDraws=true sptTokens=true malformedRefused=true truncatedRefused=true budgets=true deterministic=true " +
            $"branchVertices={tree0.BranchVertices.Count} leaves={tree0.Leaves.Count}");
    }

    // Trunk (8 sides x 8 segments), one branch level and a final leaf level.
    private static byte[] SyntheticSpt(float branchFrequency = 50, float leafFrequency = 400, int extraLevels = 0)
    {
        var bytes = new List<byte>();
        void Int(int value) { var data = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, value); bytes.AddRange(data); }
        void Float(float value) { var data = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(data, value); bytes.AddRange(data); }
        void Text(string value) { Int(value.Length); bytes.AddRange(Encoding.Latin1.GetBytes(value)); }
        void Token(int token, Action payload) { Int(token); payload(); }
        string Spline(float minimum, float maximum, float variance, float start = 1, float end = 1) =>
            $"BezierSpline {minimum}\t{maximum}\t{variance}\n{{\n\n\t2\n\t0 {start} 0.707107 0.707107 0.1\n\t1 {end} 0.707107 0.707107 0.1\n\n}}\n";
        void Level(float length, float radius, float angle, int sides, int segments, float frequency, float lower, float upper)
        {
            Int(1016);
            Token(6000, () => Text(Spline(0, 1, 10)));
            Token(6001, () => Text(Spline(.2f, .2f, 0)));
            Token(6002, () => Text(Spline(.2f, .2f, 0)));
            Token(6003, () => Text(Spline(0, 1, 0, 0, 1)));
            Token(6004, () => Text(Spline(length, length, .01f)));
            Token(6005, () => Text(Spline(radius, radius, 0)));
            Token(6006, () => Text(Spline(0, 1, 0, 1, .2f)));
            Token(6007, () => Text(Spline(angle, angle, 5)));
            Token(6008, () => Int(sides));
            Token(6009, () => Int(segments));
            Token(6010, () => Float(lower));
            Token(6011, () => Float(upper));
            Token(6012, () => Float(frequency));
            Token(6013, () => Float(4));
            Token(6014, () => Float(8));
            Token(6015, () => bytes.Add(1));
            Token(6016, () => bytes.Add(0));
            Token(6017, () => Text(Spline(0, 1, 0, .5f, 1)));
            Int(1017);
        }
        Token(1000, () => Text("__IdvSpt_02_"));
        Int(1002);
        Token(2000, () => Text("C:\\Trees\\Shrub\\\\ShrubBark.tga"));
        Token(2001, () => Float(600));
        Token(2002, () => bytes.Add(0));
        Token(2003, () => Float(100));
        Token(2005, () => Int(99));
        Token(2006, () => Float(200));
        Token(2007, () => Float(10));
        Token(1014, () => Int(3 + extraLevels));
        Level(.8f, .02f, -90, 8, 8, branchFrequency, .3f, .95f);
        for (var index = 0; index < extraLevels; index++) Level(.2f, .006f, 45, 6, 3, 10, .1f, .9f);
        Level(.2f, .006f, 45, 6, 3, leafFrequency, .1f, .9f);
        Level(.05f, .001f, 30, 4, 1, 0, 0, 1);
        Int(1015);
        Int(1003);
        Int(1004);
        Token(3000, () => Float(.9f));
        Int(1009); Int(1006); Int(1);
        Int(1007);
        Token(4000, () => bytes.Add(0));
        Token(4001, () => { Float(1); Float(1); Float(1); });
        Token(4003, () => Text("Leaf.tga"));
        Token(4004, () => { Float(.5f); Float(.5f); Float(0); });
        Token(4005, () => { Float(.1f); Float(.1f); Float(0); });
        Int(1008);
        Int(1010);
        Int(1005);
        Int(1011);
        Token(5004, () => { Float(.005f); Float(.5f); Float(0); });
        Token(5006, () => bytes.Add(1));
        Int(1012);
        Int(1001);
        Int(10000);
        Token(10002, () => { Int(1); foreach (var value in new float[] { 1, 0, 0, 0, 0, 1, 1, 1 }) Float(value); });
        Token(10003, () => Int(0));
        Int(10001);
        Int(16000);
        Token(16002, () => Float(2)); Token(16003, () => Int(5));
        Token(16002, () => Float(1)); Token(16003, () => Int(0));
        Int(16001);
        Token(21000, () => Float(.25f));
        return [.. bytes];
    }

    private static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }

    private static void Reject<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not raised.");
    }
}
