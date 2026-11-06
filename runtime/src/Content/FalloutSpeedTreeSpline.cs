using System.Globalization;
using System.Numerics;

namespace OpenNV.Runtime.Content;

// SpeedTree's shared generator: a Park-Miller minimal-standard step (Schrage's
// method) behind a 128-entry Bays-Durham shuffle. Every spline evaluation
// draws from it, so the evaluation order determines a tree's shape.
internal sealed class FalloutSpeedTreeRandom
{
    private const int TableSize = 128;
    private readonly double[] _table = new double[TableSize];
    private int _state;

    internal FalloutSpeedTreeRandom(int seed) => Seed(seed);

    internal void Seed(int seed)
    {
        _state = Math.Max(seed, 1);
        for (var index = 0; index < TableSize; index++) _table[index] = Raw();
    }

    internal float Uniform(float minimum, float maximum)
    {
        var index = (int)(Raw() * TableSize);
        var sample = _table[index];
        _table[index] = Raw();
        return (float)(minimum + (maximum - minimum) * sample);
    }

    private double Raw()
    {
        var high = _state / 127773;
        var low = _state - high * 127773;
        _state = 16807 * low - 2836 * high;
        if (_state <= 0) _state += 2147483647;
        return _state * 4.656612875e-10;
    }
}

// A text Bezier spline ("BezierSpline min max variance { n, n x y tx ty len }")
// resampled into a 500-entry lookup table indexed by x.
internal sealed class FalloutSpeedTreeSpline
{
    private const int Entries = 500;
    private readonly float[] _table = new float[Entries];
    internal float Minimum { get; }
    internal float Maximum { get; }
    internal float Variance { get; }

    private FalloutSpeedTreeSpline(float minimum, float maximum, float variance, IReadOnlyList<(Vector2 Point, Vector2 Tangent, float Length)> points)
    {
        (Minimum, Maximum, Variance) = (minimum, maximum, variance);
        var samples = new Vector2[Entries];
        for (var index = 0; index < Entries; index++) samples[index] = Curve(points, index / (float)(Entries - 1));
        var cursor = 0;
        for (var entry = 0; entry < Entries; entry++)
        {
            var x = entry / (float)(Entries - 1);
            while (cursor < Entries - 2 && samples[cursor + 1].X < x) cursor++;
            var (a, b) = (samples[cursor], samples[cursor + 1]);
            _table[entry] = x <= a.X ? a.Y : x >= b.X ? b.Y : a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X);
        }
        _table[0] = points[0].Point.Y;
        _table[^1] = points[^1].Point.Y;
    }

    internal static FalloutSpeedTreeSpline Parse(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        float Number(int index) => index < words.Length && float.TryParse(words[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            float.IsFinite(value) ? value : throw new InvalidDataException("SpeedTree spline has a malformed number.");
        if (words.Length < 6 || words[0] != "BezierSpline" || words[4] != "{" || words[^1] != "}")
            throw new InvalidDataException("SpeedTree spline is not a BezierSpline block.");
        var count = (int)Number(5);
        if (count < 2 || words.Length != 7 + count * 5) throw new InvalidDataException("SpeedTree spline control-point count is malformed.");
        var points = new (Vector2, Vector2, float)[count];
        for (var index = 0; index < count; index++)
        {
            var at = 6 + index * 5;
            var tangent = new Vector2(Number(at + 2), Number(at + 3));
            if (tangent.LengthSquared() <= 0) throw new InvalidDataException("SpeedTree spline tangent is zero.");
            points[index] = (new(Number(at), Number(at + 1)), Vector2.Normalize(tangent), Number(at + 4));
        }
        return new(Number(1), Number(2), Number(3), points);
    }

    // min + (max - min) * y(x) + U(-variance, variance); always one draw.
    internal float Evaluate(float x, FalloutSpeedTreeRandom random)
    {
        x = Math.Clamp(x, 0, 1);
        var index = Math.Min((int)(x * (Entries - 1)), Entries - 2);
        var y = _table[index] + (_table[index + 1] - _table[index]) * (x - index / (float)(Entries - 1)) * (Entries - 1);
        return Minimum + (Maximum - Minimum) * y + random.Uniform(-Variance, Variance);
    }

    // The disturbance sample scales the variance by the table value at x.
    internal float Disturbance(float x, FalloutSpeedTreeRandom random)
    {
        var y = _table[(int)MathF.Round(Math.Clamp(x, 0, 1) * (Entries - 1))];
        return random.Uniform(-Variance * y, Variance * y);
    }

    private static Vector2 Curve(IReadOnlyList<(Vector2 Point, Vector2 Tangent, float Length)> points, float t)
    {
        var scaled = t * (points.Count - 1);
        var segment = Math.Min((int)scaled, points.Count - 2);
        var u = scaled - segment;
        var (start, end) = (points[segment], points[segment + 1]);
        var p0 = start.Point;
        var p1 = start.Point + start.Tangent * start.Length;
        var p2 = end.Point - end.Tangent * end.Length;
        var p3 = end.Point;
        var a = Vector2.Lerp(p0, p1, u); var b = Vector2.Lerp(p1, p2, u); var c = Vector2.Lerp(p2, p3, u);
        var d = Vector2.Lerp(a, b, u); var e = Vector2.Lerp(b, c, u);
        return Vector2.Lerp(d, e, u);
    }
}
