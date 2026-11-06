using System.Numerics;
using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;

namespace OpenNV.Runtime.Content;

// One physics hit along a probe segment. Fraction is the hit's share of the
// segment; Reference is the placed reference owning the struck scene object.
internal readonly record struct FalloutCameraProbeHit(FalloutFormKey? Reference, float Fraction, bool Actor);

// Live scene adapter for VATS camera-path probes, in game units. Pose heading
// is measured clockwise from +Y. Cast reports every hit between two points.
internal sealed record FalloutVatsCameraSpace(Func<FalloutFormKey, (Vector3 Position, float Heading)> Pose,
    Func<FalloutFormKey, Vector3?> BoundCenter, Func<Vector3, Vector3, IReadOnlyList<FalloutCameraProbeHit>> Cast);

// Side clearance and target sight for VATS camera paths (CPTH conditions).
internal static class FalloutVatsCameraProbe
{
    internal const float AreaProbeLength = 10000;
    private const float BlockingFraction = .85f;
    private const float ActorBlockingDistance = 256;

    internal static float SideAngle(Function function) => function switch
    {
        Function.GetVATSFrontAreaFree or Function.GetVATSFrontTargetVisible => 0,
        Function.GetVATSRightAreaFree or Function.GetVATSRightTargetVisible => MathF.PI / 2,
        Function.GetVATSBackAreaFree => MathF.PI,
        Function.GetVATSLeftAreaFree or Function.GetVATSLeftTargetVisible => 3 * MathF.PI / 2,
        _ => throw new InvalidDataException($"Condition {function} is not a VATS camera probe."),
    };

    internal static Vector3 Direction(float heading, float side) =>
        new(MathF.Sin(heading + side), MathF.Cos(heading + side), 0);

    // Distance to the nearest obstruction owned by another reference, or the
    // largest float when the whole probe is clear.
    internal static float AreaFree(FalloutVatsCameraSpace space, FalloutFormKey subject, float side, float height)
    {
        var (position, heading) = space.Pose(subject);
        var origin = position + new Vector3(0, 0, height);
        var hits = space.Cast(origin, origin + Direction(heading, side) * AreaProbeLength);
        var nearest = float.MaxValue;
        foreach (var hit in hits)
            if (hit.Reference is { } owner && owner != subject) nearest = MathF.Min(nearest, hit.Fraction * AreaProbeLength);
        return nearest;
    }

    // The farthest lateral step, up to stepCount × stepDistance, from which the
    // target's bound centre is still in view; zero when the first step is blocked.
    internal static float TargetVisible(FalloutVatsCameraSpace space, FalloutFormKey subject, FalloutFormKey target,
        float side, float height, float stepDistance, int stepCount)
    {
        if (!float.IsFinite(stepDistance) || stepDistance <= 0 || stepCount < 0)
            throw new InvalidDataException("VATS camera step settings are invalid.");
        if (space.BoundCenter(target) is not { } aim) return 0;
        var (position, heading) = space.Pose(subject);
        var origin = position + new Vector3(0, 0, height);
        var direction = Direction(heading, side);
        float result = 0;
        for (var step = 1; step <= stepCount; step++)
        {
            var start = origin + direction * (step * stepDistance);
            var length = Vector3.Distance(start, aim);
            if (space.Cast(start, aim).Any(hit => hit.Reference is { } owner && owner != target && owner != subject &&
                hit.Fraction <= BlockingFraction && (!hit.Actor || length * hit.Fraction <= ActorBlockingDistance))) break;
            result = step * stepDistance;
        }
        return result;
    }
}
