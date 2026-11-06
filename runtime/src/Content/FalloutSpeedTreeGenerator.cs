using System.Numerics;

namespace OpenNV.Runtime.Content;

// Shade is the branch self-shadow multiplier; Wind is the per-vertex wind
// weight and WindMatrix the index (0-3) of the wind matrix the vertex follows.
internal readonly record struct FalloutSpeedTreeBranchVertex(Vector3 Position, Vector3 Normal, Vector2 Uv, float Shade, float Wind, int WindMatrix);

// One camera-facing leaf card anchor. Corners are the card offsets in the
// card's (right, up) plane in game units; Uvs are the matching atlas corners.
// Group is the 0-47 rocking/table index of the first corner; Lightness is the
// card's light level (0-0.99).
internal sealed record FalloutSpeedTreeLeaf(Vector3 Position, Vector3 Normal, IReadOnlyList<Vector2> Corners, IReadOnlyList<Vector2> Uvs,
    int Group, float Lightness, float Wind, int WindMatrix);

internal sealed record FalloutSpeedTreeGeometry(
    int Seed,
    float Size,
    IReadOnlyList<FalloutSpeedTreeBranchVertex> BranchVertices,
    IReadOnlyList<int> BranchIndices,
    IReadOnlyList<FalloutSpeedTreeLeaf> Leaves);

// Generates a tree's branches and leaf anchors from its source levels. Game
// units, +Z up; a branch grows along its frame's local +X. Rotations are
// degrees; local rotations pre-multiply the 3x3 row-vector frame. The final
// level is never meshed: branches of the level before it carry the leaves.
internal sealed class FalloutSpeedTreeGenerator
{
    private const int WindMatrices = 4;
    private const float LeafLightnessCap = .99f;
    // Generation budgets: shipped trees stay well below them; a source that
    // exceeds one is malformed rather than silently truncated.
    internal const int MaximumBranchVertices = 200_000;
    internal const int MaximumLeaves = 50_000;
    private const int MaximumChildren = 4096;
    private readonly FalloutSpeedTreeSource _source;
    private readonly FalloutSpeedTreeRandom _random;
    private readonly float _size;
    private readonly float _branchDimming;
    private readonly List<FalloutSpeedTreeBranchVertex> _vertices = [];
    private readonly List<int> _indices = [];
    private readonly List<(Vector3 Position, int Map, bool Mirror, float Wind, int Matrix)> _anchors = [];
    private int _windCounter;

    private FalloutSpeedTreeGenerator(FalloutSpeedTreeSource source, int seed, float sizeConversion, float branchDimming)
    {
        _source = source;
        _random = new(seed);
        // The tree size varies once per seed, before any branch is built.
        _size = (source.Size + _random.Uniform(-source.SizeVariance, source.SizeVariance)) * sizeConversion;
        _branchDimming = branchDimming;
    }

    internal static FalloutSpeedTreeGeometry Generate(FalloutSpeedTreeSource source, int seed, float sizeConversion, float branchDimming)
    {
        if (!float.IsFinite(sizeConversion) || sizeConversion <= 0) throw new ArgumentOutOfRangeException(nameof(sizeConversion));
        if (source.LeafMaps.Count == 0) throw new NotSupportedException("SpeedTree source has no leaf maps.");
        var generator = new FalloutSpeedTreeGenerator(source, seed, sizeConversion, Math.Clamp(branchDimming, 0, 1));
        if (!(generator._size > 0)) throw new InvalidDataException("SpeedTree size is not positive.");
        generator.Branch(0, Vector3.Zero, 0, Frame.Identity, float.PositiveInfinity, seed, 0, 1, -1);
        return new(seed, generator._size, generator._vertices, generator._indices, generator.Leaves());
    }

    private void Branch(int levelIndex, Vector3 origin, float age, Frame parent, float radiusCap, int seed, float fade, float parentWind, int windMatrix)
    {
        var level = _source.Levels[levelIndex];
        var length = level.Length.Evaluate(age, _random) * _size;
        var roll = _random.Uniform(-180, 180);
        var startAngle = level.StartAngle.Evaluate(age, _random);
        var gravity = level.Gravity.Evaluate(age, _random);
        var radius = level.Radius.Evaluate(age, _random) * _size;
        var flexibility = level.Flexibility.Evaluate(age, _random);
        if (.85f * radiusCap < level.RadiusProfile.Evaluate(0, _random) * radius) radius = .85f * radiusCap;
        if (!float.IsFinite(length) || !float.IsFinite(radius) || !float.IsFinite(startAngle) || !float.IsFinite(gravity) || !float.IsFinite(flexibility))
            throw new InvalidDataException("SpeedTree branch parameters are not finite.");
        if (length <= 0 || radius < 0) return;
        // The trunk opens the wind level: it and its whole sub-tree follow one matrix.
        if (levelIndex == 0) windMatrix = _windCounter++ % WindMatrices;
        var second = level.Disturbance.Disturbance(0, _random);
        var first = level.Disturbance.Disturbance(0, _random);
        // The roll turns about the parent axis in world space; the trunk's
        // parent axis is the tree's up axis.
        var parentAxis = levelIndex == 0 ? Vector3.UnitZ : Vector3.Normalize(parent.X);
        var frame = Frame.PitchYaw(startAngle + first, second) * parent * Frame.AxisAngle(roll, parentAxis);
        frame = Bend(frame, level, gravity, 0);

        var segments = level.Segments;
        var frames = new Frame[segments + 1];
        var centres = new Vector3[segments + 1];
        var radii = new float[segments + 1];
        var travel = new float[segments + 1];
        var winds = new float[segments + 1];
        (frames[0], centres[0]) = (frame, origin);
        for (var index = 0; index <= segments; index++)
        {
            var t = index / (float)segments;
            if (index > 0)
            {
                centres[index] = centres[index - 1] + frames[index - 1].X * (t * length - travel[index - 1]);
                travel[index] = t * length;
                var bent = Bend(frames[index - 1], level, gravity, t);
                var a = level.Disturbance.Disturbance(t, _random);
                var b = level.Disturbance.Disturbance(t, _random);
                frames[index] = Frame.PitchYaw(b, a) * bent;
            }
            radii[index] = Math.Max(0, level.RadiusProfile.Evaluate(t, _random) * radius);
            var profile = level.FlexibilityProfile.Evaluate(t, _random);
            winds[index] = levelIndex == 0 ? 1 - t * flexibility * profile : parentWind * (1 - flexibility * profile);
        }
        Mesh(level, frames, centres, radii, winds, length, radius, fade, windMatrix);

        var children = MathF.Floor(level.Frequency / _size * length);
        if (!float.IsFinite(children) || children > MaximumChildren)
            throw new InvalidDataException("SpeedTree branch child count exceeds its budget.");
        var count = (int)children;
        var leaves = levelIndex + 2 == _source.Levels.Count;
        for (var child = 0; child < count; child++)
        {
            if (!leaves) { seed += 3; _random.Seed(seed); }
            var lower = level.Lower; var upper = level.Upper;
            // The first child is a leader near the tip.
            var position = child == 0
                ? _random.Uniform(lower + (upper - lower) * .85f, lower + (upper - lower) * .95f)
                : _random.Uniform(lower, upper);
            var distance = Math.Clamp(position, 0, 1) * length;
            var segment = 0;
            while (segment < segments - 1 && travel[segment + 1] < distance) segment++;
            var span = travel[segment + 1] - travel[segment];
            var blend = span > 0 ? Math.Clamp((distance - travel[segment]) / span, 0, 1) : 0;
            var at = Vector3.Lerp(centres[segment], centres[segment + 1], blend);
            var wind = winds[segment] + (winds[segment + 1] - winds[segment]) * blend;
            if (leaves)
            {
                var map = Math.Min((int)_random.Uniform(0, _source.LeafMaps.Count), _source.LeafMaps.Count - 1);
                _anchors.Add((at, map, _random.Uniform(0, 1) < .5f, wind, windMatrix));
                if (_anchors.Count > MaximumLeaves) throw new InvalidDataException("SpeedTree leaves exceed the generation budget.");
                continue;
            }
            _random.Seed(seed);
            _ = _random.Uniform(0, 100);
            var childAge = upper > lower ? (position - lower) / (upper - lower) : 1;
            var childFade = fade + (1 - fade) * childAge;
            if (levelIndex + 1 <= 1) childFade *= childFade;
            Branch(levelIndex + 1, at, childAge, frames[segment], radii[segment] + (radii[segment + 1] - radii[segment]) * blend,
                seed, childFade, wind, windMatrix);
        }
    }

    // Gravity bends the frame toward or away from +Z about the world axis
    // dir x Z, scaled by how close the direction is to horizontal.
    private Frame Bend(Frame frame, FalloutSpeedTreeLevel level, float gravity, float t)
    {
        var profile = level.GravityProfile.Evaluate(t, _random);
        var direction = Vector3.Normalize(frame.X);
        var up = MathF.Acos(Math.Clamp(direction.Z, -1, 1)) * 180 / MathF.PI;
        var falloff = 1 - MathF.Abs(90 - up) / 90;
        var axis = Vector3.Cross(direction, Vector3.UnitZ);
        if (axis.LengthSquared() < 1e-10f) return frame;
        return frame * Frame.AxisAngle(-2 * (profile - .5f) * gravity * up * falloff, Vector3.Normalize(axis));
    }

    private void Mesh(FalloutSpeedTreeLevel level, Frame[] frames, Vector3[] centres, float[] radii, float[] winds, float length, float radius,
        float fade, int windMatrix)
    {
        var sides = level.Sides;
        var uRepeat = level.UAbsolute ? level.URepeat : level.URepeat * radius * 2 * MathF.PI;
        var vRepeat = level.VAbsolute ? level.VRepeat : length / _size * level.VRepeat;
        var first = _vertices.Count;
        if (first + (long)frames.Length * (sides + 1) > MaximumBranchVertices)
            throw new InvalidDataException("SpeedTree branch vertices exceed the generation budget.");
        for (var ring = 0; ring < frames.Length; ring++)
        {
            var t = ring / (float)(frames.Length - 1);
            var light = (1 - fade) * t + fade;
            var shade = light * light * _branchDimming + (1 - _branchDimming);
            for (var side = 0; side <= sides; side++)
            {
                var angle = 2 * MathF.PI * side / sides;
                var normal = Vector3.Normalize(MathF.Cos(angle) * frames[ring].Y + MathF.Sin(angle) * frames[ring].Z);
                _vertices.Add(new(centres[ring] + normal * radii[ring], normal, new(side / (float)sides * uRepeat, t * vRepeat),
                    shade, winds[ring], windMatrix));
            }
        }
        for (var ring = 0; ring < frames.Length - 1; ring++)
            for (var side = 0; side < sides; side++)
            {
                var a = first + ring * (sides + 1) + side; var b = a + sides + 1;
                _indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
    }

    // Card normals point away from the canopy centre; cards nearer the centre
    // are darker. Card size is the map size scaled by the tree size.
    private FalloutSpeedTreeLeaf[] Leaves()
    {
        if (_anchors.Count == 0) return [];
        var centre = _anchors.Aggregate(Vector3.Zero, (sum, anchor) => sum + anchor.Position) / _anchors.Count;
        var reach = Math.Max(_anchors.Max(anchor => Vector3.Distance(anchor.Position, centre)), 1e-3f);
        var distinctTextures = _source.LeafMaps.Select(map => map.Texture).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return _anchors.Select(anchor =>
        {
            var map = _source.LeafMaps[anchor.Map];
            var outward = anchor.Position - centre;
            var normal = outward.LengthSquared() > 1e-8f ? Vector3.Normalize(outward) : Vector3.UnitZ;
            var origin = new Vector2(anchor.Mirror ? 1 - map.Origin.X : map.Origin.X, map.Origin.Y);
            var size = map.Size * _size;
            Vector2[] corners =
            [
                new((1 - origin.X) * size.X, (1 - origin.Y) * size.Y), new(-origin.X * size.X, (1 - origin.Y) * size.Y),
                new(-origin.X * size.X, -origin.Y * size.Y), new((1 - origin.X) * size.X, -origin.Y * size.Y),
            ];
            // Several distinct leaf textures read V in mirrored corner order;
            // a mirrored card swaps its left and right U values.
            var uvs = Enumerable.Range(0, 4).Select(corner => new Vector2(
                map.Corners[anchor.Mirror ? corner ^ 1 : corner].X,
                map.Corners[distinctTextures > 1 ? 3 - corner : corner].Y)).ToArray();
            var lightness = Math.Min(.5f + .5f * Vector3.Distance(anchor.Position, centre) / reach, LeafLightnessCap);
            return new FalloutSpeedTreeLeaf(anchor.Position, normal, corners, uvs, 4 * (2 * anchor.Map + (anchor.Mirror ? 1 : 0)), lightness,
                anchor.Wind, anchor.Matrix);
        }).ToArray();
    }

    private readonly record struct Frame(Vector3 X, Vector3 Y, Vector3 Z)
    {
        internal static Frame Identity => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        // Row-vector product: rows of the result are this frame's rows times right.
        public static Frame operator *(Frame left, Frame right) => new(right.Apply(left.X), right.Apply(left.Y), right.Apply(left.Z));

        private Vector3 Apply(Vector3 value) => value.X * X + value.Y * Y + value.Z * Z;

        // Rotation about local Y by a, then local Z by b (degrees).
        internal static Frame PitchYaw(float a, float b)
        {
            var (sa, ca) = MathF.SinCos(a * MathF.PI / 180);
            var (sb, cb) = MathF.SinCos(b * MathF.PI / 180);
            return new(new(ca * cb, ca * sb, -sa), new(-sb, cb, 0), new(sa * cb, sa * sb, ca));
        }

        internal static Frame AxisAngle(float degrees, Vector3 u)
        {
            var (s, c) = MathF.SinCos(degrees * MathF.PI / 180);
            var k = 1 - c;
            return new(
                new(c + k * u.X * u.X, k * u.X * u.Y + s * u.Z, k * u.X * u.Z - s * u.Y),
                new(k * u.X * u.Y - s * u.Z, c + k * u.Y * u.Y, k * u.Y * u.Z + s * u.X),
                new(k * u.X * u.Z + s * u.Y, k * u.Y * u.Z - s * u.X, c + k * u.Z * u.Z));
        }
    }
}
