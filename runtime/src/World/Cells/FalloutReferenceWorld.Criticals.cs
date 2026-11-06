using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// Script-visible combat state outside the hit pipeline: an actor's critical
// effect stage and a destructible reference's VATS targetability override.
internal sealed partial class FalloutReferenceWorld
{
    private bool IsActorReference(FalloutFormKey reference) =>
        reference == _enginePlayer || records.GetEffective(Get(reference).Base).Signature is "NPC_" or "CREA";

    // A non-actor reference has no stage; IsInCriticalStage reads it as None.
    internal byte CriticalStage(FalloutFormKey reference) =>
        IsActorReference(reference) ? _actorOverrides.GetValueOrDefault(reference)?.CriticalStage ?? 0 : FalloutCriticalStage.None;

    internal void SetCriticalStage(FalloutFormKey reference, byte stage)
    {
        stage = FalloutCriticalStage.Require(stage);
        if (!IsActorReference(reference)) return;
        _actorOverrides[reference] = Overrides(reference) with { CriticalStage = stage };
    }

    // VATS target candidates are non-actor references with destructible data.
    private FalloutDestructible? VatsTargetSource(FalloutReferenceInstance instance) =>
        records.GetEffective(instance.Base).Signature is "NPC_" or "CREA" ? null : FalloutDestructible.Read(records, instance.Base);

    internal void RequireVatsTargetCandidate(FalloutReferenceInstance instance)
    {
        if (VatsTargetSource(instance) is null) throw new InvalidDataException("Saved VATS target override has no destructible source.");
    }

    internal bool IsVatsTargetable(FalloutFormKey reference)
    {
        if (reference == _enginePlayer) return false;
        var instance = Get(reference);
        return VatsTargetSource(instance) is { } source && source.VatsTargetable != instance.VatsTargetInverted;
    }

    // Ref.SetVATSTarget Flag stores only a difference from the base flag.
    internal void SetVatsTarget(FalloutFormKey reference, bool targetable)
    {
        if (reference == _enginePlayer) return;
        var instance = Get(reference);
        if (VatsTargetSource(instance) is not { } source) return;
        instance.VatsTargetInverted = targetable != source.VatsTargetable;
        FalloutScriptCommandDeferral.Warn("setvatstarget", "destructible-vats-target-admission-owner-unbound");
    }
}
