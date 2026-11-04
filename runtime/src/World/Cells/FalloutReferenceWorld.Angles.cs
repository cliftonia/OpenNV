using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceWorld
{
    // SetAngle is an exact source-space rotation edit in degrees. It uses the
    // same live-placement admission as SetPos and publishes a placement revision.
    internal void SetAngleAxis(FalloutFormKey reference, int axis, float degrees)
    {
        if (reference == records.RuntimeFormKey(0x14))
            throw new NotSupportedException("Player SetAngle requires its queued player-axis transform owner.");
        if (axis is < 0 or > 2 || !float.IsFinite(degrees)) throw new InvalidDataException("Reference angle axis/value is invalid.");
        var state = Get(reference);
        var placement = Placement(reference);
        if (state.QuerySpatialPlacement is { } live)
        {
            var observed = live(); observed.Validate();
            if (observed.Cell != placement.Cell) throw new InvalidDataException("SetAngle live reference has a different authoritative cell.");
            placement = observed.Copy();
        }
        else if (state.PackageMotion is not null || state.Engagement is not null || state.SandboxAdmissionFailure is not null)
            throw new NotSupportedException("Cached actor SetAngle requires its current source-space placement owner.");
        var radians = degrees % 360f * MathF.PI / 180f;
        if (radians < 0) radians += 2 * MathF.PI;
        placement.RotationRadians[axis] = radians;
        SetPlacement(reference, placement);
    }
}
