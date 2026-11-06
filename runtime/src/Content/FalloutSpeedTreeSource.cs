using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace OpenNV.Runtime.Content;

internal sealed record FalloutSpeedTreeLevel(
    FalloutSpeedTreeSpline Disturbance,
    FalloutSpeedTreeSpline Gravity,
    FalloutSpeedTreeSpline Flexibility,
    FalloutSpeedTreeSpline FlexibilityProfile,
    FalloutSpeedTreeSpline Length,
    FalloutSpeedTreeSpline Radius,
    FalloutSpeedTreeSpline RadiusProfile,
    FalloutSpeedTreeSpline StartAngle,
    FalloutSpeedTreeSpline GravityProfile,
    int Sides,
    int Segments,
    float Lower,
    float Upper,
    float Frequency,
    float URepeat,
    float VRepeat,
    bool UAbsolute,
    bool VAbsolute);

// One leaf map: a card colour, its authoring texture, the card pivot and size
// (fractions of the card and of the tree size) and its four atlas corners.
internal sealed record FalloutSpeedTreeLeafMap(Vector3 Color, string Texture, Vector2 Origin, Vector2 Size, IReadOnlyList<Vector2> Corners);

// SpeedTree 4 .spt container: a little-endian token stream whose payload type
// is fixed per token. Unknown tokens inside any section are malformed. Only the
// values the generator and renderer consume are retained; every other payload
// is still read and type-checked so the stream stays aligned.
internal sealed record FalloutSpeedTreeSource(
    string BarkTexture,
    float LodFar,
    float LodNear,
    int Seed,
    float Size,
    float SizeVariance,
    IReadOnlyList<FalloutSpeedTreeLevel> Levels,
    IReadOnlyList<FalloutSpeedTreeLeafMap> LeafMaps)
{
    private const string Magic = "__IdvSpt_02_";
    private enum Kind { Int, Float, Bool, Text, Vector, Floats8, Floats13, Coordinates, Sphere, Capsule, Box }

    private static readonly Dictionary<int, Kind> General = new()
    {
        [2000] = Kind.Text, [2001] = Kind.Float, [2002] = Kind.Bool, [2003] = Kind.Float, [2004] = Kind.Int,
        [2005] = Kind.Int, [2006] = Kind.Float, [2007] = Kind.Float,
    };
    private static readonly Dictionary<int, Kind> Level = Range(6000, 6007, Kind.Text).Append(new(6017, Kind.Text))
        .Append(new(6008, Kind.Int)).Append(new(6009, Kind.Int)).Concat(Range(6010, 6014, Kind.Float))
        .Append(new(6015, Kind.Bool)).Append(new(6016, Kind.Bool)).ToDictionary();
    private static readonly Dictionary<int, Kind> Leaves = new()
    {
        [3000] = Kind.Float, [3001] = Kind.Int, [3002] = Kind.Float, [3003] = Kind.Bool, [3004] = Kind.Float, [3005] = Kind.Float,
        [3006] = Kind.Bool, [3007] = Kind.Float, [3008] = Kind.Int, [3009] = Kind.Bool, [3010] = Kind.Float,
    };
    private static readonly Dictionary<int, Kind> LeafTexture = new()
    {
        [4000] = Kind.Bool, [4001] = Kind.Vector, [4002] = Kind.Float, [4003] = Kind.Text, [4004] = Kind.Vector,
        [4005] = Kind.Vector, [4006] = Kind.Vector, [4007] = Kind.Float,
    };
    private static readonly Dictionary<int, Kind> Global = Range(5000, 5004, Kind.Vector).Append(new(5005, Kind.Float))
        .Append(new(5006, Kind.Bool)).ToDictionary();
    private static readonly Dictionary<int, Dictionary<int, Kind>> Blocks = new()
    {
        [8000] = new() { [8002] = Kind.Int, [8003] = Kind.Floats13, [8004] = Kind.Int, [8005] = Kind.Floats13, [8006] = Kind.Float,
            [8007] = Kind.Int, [8008] = Kind.Int, [8009] = Kind.Floats13 },
        [9000] = new() { [9002] = Kind.Int, [9003] = Kind.Float, [9004] = Kind.Float, [9009] = Kind.Float },
        [9005] = new() { [9007] = Kind.Int, [9008] = Kind.Float, [9010] = Kind.Float, [9011] = Kind.Int, [9012] = Kind.Float,
            [9013] = Kind.Float, [9014] = Kind.Float },
        [11000] = new() { [11002] = Kind.Int },
        [10000] = new() { [10002] = Kind.Coordinates, [10003] = Kind.Coordinates, [10004] = Kind.Coordinates, [10005] = Kind.Text,
            [10006] = Kind.Bool, [10007] = Kind.Bool },
        [12000] = new() { [12002] = Kind.Sphere, [12003] = Kind.Capsule, [12004] = Kind.Box },
        [13000] = new() { [13002] = Kind.Int, [13003] = Kind.Int, [13004] = Kind.Int, [13005] = Kind.Text, [13006] = Kind.Int,
            [13007] = Kind.Bool, [13008] = Kind.Int, [13009] = Kind.Int, [13010] = Kind.Float, [13011] = Kind.Float,
            [13012] = Kind.Float, [13013] = Kind.Float, [14007] = Kind.Int, [14008] = Kind.Int },
        [14000] = new() { [14002] = Kind.Text, [14003] = Kind.Float, [14004] = Kind.Float, [14005] = Kind.Float, [14006] = Kind.Float },
        [15000] = new() { [15002] = Kind.Bool, [15003] = Kind.Float },
        [16000] = Range(16004, 16012, Kind.Float).Append(new(16002, Kind.Float)).Append(new(16003, Kind.Int)).ToDictionary(),
        [18000] = new() { [18002] = Kind.Vector, [18003] = Kind.Vector, [18004] = Kind.Vector, [18005] = Kind.Text },
        [19000] = new() { [19002] = Kind.Text },
        [20000] = new() { [20002] = Kind.Text, [20003] = Kind.Bool, [20004] = Kind.Bool, [20005] = Kind.Floats8 },
    };
    // Single values that follow the end-of-tree token outside any block.
    private static readonly Dictionary<int, Kind> Trailing = new()
    {
        [16013] = Kind.Int, [16014] = Kind.Float, [21000] = Kind.Float, [21001] = Kind.Float, [22000] = Kind.Bool,
    };

    internal static FalloutSpeedTreeSource Read(ReadOnlySpan<byte> bytes) => new Reader(bytes.ToArray()).Read();

    private static IEnumerable<KeyValuePair<int, Kind>> Range(int first, int last, Kind kind) =>
        Enumerable.Range(first, last - first + 1).Select(token => new KeyValuePair<int, Kind>(token, kind));

    private sealed class Reader(byte[] data)
    {
        private int _offset;

        internal FalloutSpeedTreeSource Read()
        {
            if (Int() != 1000 || Text() != Magic) throw new InvalidDataException("SpeedTree source is not an __IdvSpt_02_ container.");
            Dictionary<int, object>? general = null, leaves = null;
            var levels = new List<Dictionary<int, object>>();
            var leafTextures = new List<Dictionary<int, object>>();
            for (var token = Int(); token != 1001; token = Int())
            {
                switch (token)
                {
                    case 1002 when general is null:
                        general = Section(1003, General, nested =>
                        {
                            if (nested != 1014) return false;
                            var count = Count();
                            for (var index = 0; index < count; index++)
                            {
                                Expect(1016);
                                levels.Add(Section(1017, Level));
                            }
                            Expect(1015);
                            return true;
                        });
                        break;
                    case 1004 when leaves is null:
                        leaves = Section(1005, Leaves, nested =>
                        {
                            if (nested != 1009) return false;
                            Expect(1006);
                            var count = Count();
                            for (var index = 0; index < count; index++)
                            {
                                Expect(1007);
                                leafTextures.Add(Section(1008, LeafTexture));
                            }
                            Expect(1010);
                            return true;
                        });
                        break;
                    case 1011:
                        _ = Section(1012, Global);
                        break;
                    default:
                        throw new InvalidDataException($"SpeedTree source has a malformed tree section token {token}.");
                }
            }
            IReadOnlyList<float>? leafCorners = null;
            while (_offset < data.Length)
            {
                var token = Int();
                if (Trailing.TryGetValue(token, out var single)) { _ = Value(single); continue; }
                if (!Blocks.TryGetValue(token, out var kinds) || token is 9005 or 14000)
                    throw new InvalidDataException($"SpeedTree source has a malformed block token {token}.");
                var block = Section(token + 1, kinds, nested =>
                {
                    if (token == 9000 && nested == 9005) { _ = Section(9006, Blocks[9005]); return true; }
                    if (token == 13000 && nested == 14000) { _ = Section(14001, Blocks[14000]); return true; }
                    return false;
                }, token is 12000 or 15000 or 16000);
                if (token == 10000) leafCorners = (IReadOnlyList<float>)block[10002];
            }
            if (general is null || leaves is null) throw new InvalidDataException("SpeedTree source has no general tree or leaf section.");
            if (levels.Count < 2) throw new NotSupportedException("SpeedTree source needs a geometry level and a leaf level.");
            var maps = leafTextures.Select((texture, index) => LeafMap(texture, index, leafCorners)).ToArray();
            var bark = (string)Required(general, 2000);
            return new(bark[(Math.Max(bark.LastIndexOf('\\'), bark.LastIndexOf('/')) + 1)..], Finite(general, 2001), Finite(general, 2003),
                (int)Required(general, 2005), Finite(general, 2006), Finite(general, 2007), levels.Select(ReadLevel).ToArray(), maps);
        }

        private static FalloutSpeedTreeLeafMap LeafMap(Dictionary<int, object> texture, int index, IReadOnlyList<float>? corners)
        {
            if (corners is null || corners.Count < (index + 1) * 8)
                throw new InvalidDataException($"SpeedTree leaf map {index} has no composite texture coordinates.");
            var origin = (Vector3)Required(texture, 4004);
            var size = (Vector3)Required(texture, 4005);
            if (!(size.X > 0 && size.Y > 0 && float.IsFinite(origin.X) && float.IsFinite(origin.Y)))
                throw new InvalidDataException($"SpeedTree leaf map {index} has no positive card size.");
            var uv = Enumerable.Range(0, 4).Select(corner => new Vector2(corners[index * 8 + corner * 2], corners[index * 8 + corner * 2 + 1])).ToArray();
            return new((Vector3)Required(texture, 4001), (string)Required(texture, 4003), new(origin.X, origin.Y), new(size.X, size.Y), uv);
        }

        private static FalloutSpeedTreeLevel ReadLevel(Dictionary<int, object> level)
        {
            FalloutSpeedTreeSpline Spline(int token) => FalloutSpeedTreeSpline.Parse((string)Required(level, token));
            int Integer(int token, int fallback) => level.TryGetValue(token, out var value) ? (int)value : fallback;
            float Number(int token, float fallback) => level.TryGetValue(token, out var value) ? (float)value : fallback;
            bool Flag(int token, bool fallback) => level.TryGetValue(token, out var value) ? (bool)value : fallback;
            var sides = Integer(6008, 6);
            var segments = Integer(6009, 3);
            if (sides is < 3 or > 64 || segments is < 1 or > 64) throw new InvalidDataException("SpeedTree level has an unbounded cross-section or segment count.");
            return new(Spline(6000), Spline(6001), Spline(6002), Spline(6003), Spline(6004), Spline(6005), Spline(6006), Spline(6007),
                Spline(6017), sides, segments, Number(6010, .3f), Number(6011, 1), Number(6012, .3f), Number(6013, 1), Number(6014, 1),
                Flag(6015, true), Flag(6016, false));
        }

        // Reads payloads until the closing token. With repeatable entries, a
        // token may occur more than once (collision objects, flares, texture
        // controls); otherwise a repeated token is malformed.
        private Dictionary<int, object> Section(int end, Dictionary<int, Kind> kinds, Func<int, bool>? nested = null, bool repeatable = false)
        {
            var values = new Dictionary<int, object>();
            for (var token = Int(); token != end; token = Int())
            {
                if (nested?.Invoke(token) == true) continue;
                if (!kinds.TryGetValue(token, out var kind)) throw new InvalidDataException($"SpeedTree source has malformed token {token} before {end}.");
                var value = Value(kind);
                if (!values.TryAdd(token, value) && !repeatable) throw new InvalidDataException($"SpeedTree source repeats token {token}.");
            }
            return values;
        }

        private object Value(Kind kind) => kind switch
        {
            Kind.Int => Int(),
            Kind.Float => Float(),
            Kind.Bool => Bool(),
            Kind.Text => Text(),
            Kind.Vector => new Vector3(Float(), Float(), Float()),
            Kind.Floats8 => Floats(8),
            Kind.Floats13 => Floats(13),
            Kind.Coordinates => Floats(checked(Count() * 8)),
            Kind.Sphere => Floats(4),
            Kind.Capsule => Floats(5),
            Kind.Box => Floats(6),
            _ => throw new InvalidOperationException(),
        };

        private float[] Floats(int count) => Enumerable.Range(0, count).Select(_ => Float()).ToArray();
        private void Expect(int token) { if (Int() != token) throw new InvalidDataException($"SpeedTree source is missing token {token}."); }

        private int Count() => Int() is var count and >= 0 and <= 4096 ? count : throw new InvalidDataException("SpeedTree source count is out of range.");

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _offset > data.Length - count) throw new InvalidDataException("SpeedTree source is truncated.");
            var span = data.AsSpan(_offset, count);
            _offset += count;
            return span;
        }

        private int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        private float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        private bool Bool() => Take(1)[0] switch { 0 => false, 1 => true, _ => throw new InvalidDataException("SpeedTree Boolean is not 0 or 1.") };
        private string Text() => Encoding.Latin1.GetString(Take(Int()));
    }

    private static object Required(Dictionary<int, object> values, int token) =>
        values.TryGetValue(token, out var value) ? value : throw new InvalidDataException($"SpeedTree source has no token {token}.");

    private static float Finite(Dictionary<int, object> values, int token) =>
        Required(values, token) is float value && float.IsFinite(value) ? value : throw new InvalidDataException($"SpeedTree value {token} is not finite.");
}
