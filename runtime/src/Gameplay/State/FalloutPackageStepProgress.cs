using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

// The step cursor of a stepped package (Wander, UseItemAt, Ambush,
// FleeNotCombat, UseWeapon) and its per-step counters. It is saved with the
// package motion so a loaded actor resumes the same step without replaying
// its start event, random volley draws or completed uses.
internal sealed record FalloutPackageStepProgress(FalloutFormKey Package, int Step, double StartedHours,
    float[]? Anchor = null, double Timer = 0, double Cooldown = 0, int Shots = 0, int ShotTarget = 0, int Volleys = 0, int Uses = 0,
    bool TargetHandled = false, bool Hidden = false, bool Seated = false, bool DoneHandled = false)
{
    internal FalloutPackageStepProgress Copy() => this with { Anchor = Anchor is null ? null : (float[])Anchor.Clone() };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Package.OwnerPlugin) || Package.ObjectId == 0 || Step is < 0 or > 3 ||
            !double.IsFinite(StartedHours) || StartedHours < 0 || !double.IsFinite(Timer) || !double.IsFinite(Cooldown) ||
            Shots < 0 || ShotTarget < 0 || Volleys < 0 || Uses < 0 ||
            Anchor is not null && (Anchor.Length != 3 || Anchor.Any(value => !float.IsFinite(value))))
            throw new InvalidDataException("Saved package step progress is invalid.");
    }
}
