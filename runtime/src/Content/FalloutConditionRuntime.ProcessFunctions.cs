using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Content;

// Combat-group, effect, weather, camera-probe and other process functions.
internal sealed partial class FalloutConditionRuntime
{
    // Functions whose first parameter is an object or actor reference. An empty
    // CTDA parameter takes the run-on's other reference.
    internal static readonly IReadOnlySet<Function> ReferenceParameterFunctions = new HashSet<Function>
    {
        Function.GetDistance, Function.GetInSameCell, Function.GetDetected, Function.GetShouldAttack, Function.GetDisposition,
        Function.GetHeadingAngle, Function.GetIsReference, Function.IsCurrentFurnitureRef, Function.Exists,
        Function.GetFactionRelation, Function.IsPlayerGrabbedRef, Function.IsCombatTarget,
        Function.GetVATSRightAreaFree, Function.GetVATSLeftAreaFree, Function.GetVATSBackAreaFree, Function.GetVATSFrontAreaFree,
        Function.GetVATSRightTargetVisible, Function.GetVATSLeftTargetVisible, Function.GetVATSFrontTargetVisible,
    };

    private const uint UnarmedSkill = 45;
    private const byte GuardPackageType = 14;

    private FalloutConditionQuery WithOtherReference(FalloutConditionQuery condition, FalloutConditionScope scope) =>
        condition.BoundForm is null && condition.Argument1 == 0 && ReferenceParameterFunctions.Contains(condition.Kind) &&
        scope.Other(condition.Declaration) is { } other ? condition with { BoundForm = other } : condition;

    private FalloutFormKey? CombatTarget(FalloutFormKey primary) => Required(world, "combat state").CombatTarget(primary);

    // Functions that need no subject reference.
    private float? EvaluateSubjectless(FalloutConditionQuery condition)
    {
        switch (condition.Kind)
        {
            case Function.IsRaining:
                NoArguments(condition);
                return FalloutWeatherPrecipitation.IsRaining(records, Required(world, "sky").WeatherTransition) ? 1 : 0;
            case Function.GetDayOfWeek:
                NoArguments(condition);
                var days = FalloutGameTimeBindings.Read(records).DaysPassed;
                if (!records.TryGetWinner(days, out var source) || source.IsDeleted) return 1;
                return (float)(((long)Math.Truncate((double)Required(Globals, "game-day global").Get(days)) % 7 + 7) % 7);
            case Function.IsPlayerMovingIntoNewSpace:
                NoArguments(condition); return Required(world, "player space transition").PlayerMovingIntoNewSpace ? 1 : 0;
            case Function.IsPlayerGrabbedRef:
                SecondUnused(condition);
                return !condition.IsNullFormArgument1(records) &&
                    Required(world, "player grab").PlayerGrabbedReference == condition.FormArgument1 ? 1 : 0;
            default: return null;
        }
    }

    private bool IsActor(FalloutFormKey subject) => subject == records.RuntimeFormKey(0x14) ||
        records.GetEffective(subject).Signature is "ACHR" or "ACRE";

    private float EvaluateProcessFunction(FalloutConditionQuery condition, FalloutFormKey subject)
    {
        var player = records.RuntimeFormKey(0x14);
        switch (condition.Kind)
        {
            case Function.GetScale:
                NoArguments(condition);
                // SetScale changes only the player; other references keep their placed XSCL.
                return subject == player ? NativeValue(subject, condition) :
                    FalloutCellSceneReader.ReadReferenceScale(records.GetEffective(subject));
            case Function.IsTurning or Function.IsIdlePlaying:
                NoArguments(condition); return IsActor(subject) ? NativeValue(subject, condition) : 0;
            case Function.GetAnimAction:
                NoArguments(condition); return IsActor(subject) ? NativeValue(subject, condition) : -1;
            case Function.GetShouldAttack:
                SecondUnused(condition);
                return condition.IsNullFormArgument1(records) ? 0 : Required(world, "combat state").ShouldAttack(subject, condition.FormArgument1);
            case Function.IsWeaponSkillType:
                SecondUnused(condition);
                var item = records.GetEffective(Base(subject));
                if (item.Signature == "WEAP") return FalloutWeaponShot.ReadSkillActorValue(item) == checked((int)condition.Argument1) ? 1 : 0;
                // No weapon fights unarmed; any other skill fails the condition.
                return condition.Argument1 == UnarmedSkill ? 1 : FalloutCondition.EvaluationFailed;
            case Function.GetTrespassWarningLevel:
                NoArguments(condition);
                if (subject == player || !IsActor(subject)) return -1;
                if ((CurrentPackage ?? Required(world, "package state").CurrentPackage)(subject) is not { } package) return -1;
                // OpenNV runs no trespass-reaction package; a guard's warnings need their procedure.
                return FalloutPackageData.Read(records.GetEffective(package)).Procedure == GuardPackageType
                    ? throw Unowned(condition, "guard warning count") : -1;
            case Function.HasMagicEffect or Function.IsSpellTarget:
                SecondUnused(condition);
                if (!IsActor(subject) || condition.IsNullFormArgument1(records)) return 0;
                var form = condition.FormArgument1;
                // Authored items never apply the engine's internal usage-monitor effect.
                return Required(world, "active effects").ActiveEffects(subject).Any(effect =>
                    (condition.Kind == Function.HasMagicEffect ? effect.Effect : effect.Source) == form) ? 1 : 0;
            case Function.GetGroupMemberCount or Function.GetGroupTargetCount:
                NoArguments(condition);
                if (!IsActor(subject) || Required(world, "combat state").CombatGroup(subject) is not { } group) return 0;
                return condition.Kind == Function.GetGroupMemberCount ? group.Members.Count : group.Targets.Count;
            case Function.GetIsObjectType:
                SecondUnused(condition);
                var signature = records.GetEffective(Base(subject)).Signature;
                return (FalloutReferenceWorld.FormTypeCode(signature) ??
                    throw new NotSupportedException($"Object type of {signature} has no qualified native identity.")) == condition.Argument1 ? 1 : 0;
            case Function.GetIsUsedItemEquipType:
                SecondUnused(condition); return Required(world, "used-item IDLE selection").QueryUsedItem(condition);
            case Function.GetRadiationLevel:
                // Irradiated water, hazards and regions have no exposure owner yet.
                NoArguments(condition); return IsActor(subject) ? throw Unowned(condition, "incoming radiation rate") : 0;
            case Function.GetMajorCrimeCount:
                NoArguments(condition); return IsActor(subject) ? throw Unowned(condition, "major crime count") : 0;
            case Function.GetFriendHit:
                NoArguments(condition); return IsActor(subject) ? throw Unowned(condition, "friendly hit record") : 0;
            case Function.HasBeenEaten:
                NoArguments(condition);
                // Conditions stop running once the player dies, so only NPC corpses are queried.
                return subject != player && IsActor(subject) && Dead(subject) ? throw Unowned(condition, "corpse eaten flag") : 0;
            case Function.GetCauseofDeath:
                NoArguments(condition);
                return subject != player && IsActor(subject) && Dead(subject) ? throw Unowned(condition, "cause of death") : -1;
            case Function.GetVATSRightAreaFree or Function.GetVATSLeftAreaFree or Function.GetVATSBackAreaFree or Function.GetVATSFrontAreaFree:
                // The parameter is accepted and ignored.
                if (!IsActor(subject)) return 0;
                return FalloutVatsCameraProbe.AreaFree(Required(world, "VATS camera").VatsConditions.CameraSpace, subject,
                    FalloutVatsCameraProbe.SideAngle(condition.Kind), records.NumericSettings.Float("fVATSSmartCameraCheckHeight"));
            case Function.GetVATSRightTargetVisible or Function.GetVATSLeftTargetVisible or Function.GetVATSFrontTargetVisible:
                SecondUnused(condition);
                if (!IsActor(subject) || condition.IsNullFormArgument1(records)) return 0;
                var steps = records.NumericSettings.Float("fVATSSmartCameraCheckStepCount");
                if (!float.IsFinite(steps) || steps < 0) throw new InvalidDataException("VATS camera step count is invalid.");
                return FalloutVatsCameraProbe.TargetVisible(Required(world, "VATS camera").VatsConditions.CameraSpace, subject,
                    condition.FormArgument1, FalloutVatsCameraProbe.SideAngle(condition.Kind),
                    records.NumericSettings.Float("fVATSSmartCameraCheckHeight"),
                    records.NumericSettings.Float("fVATSSmartCameraCheckStepDistance"), (int)steps);
            default: throw new InvalidDataException($"Condition {condition.Kind} is not a process function.");
        }
    }

    private static NotSupportedException Unowned(FalloutConditionQuery condition, string state) =>
        new($"Condition {condition.Kind} has no authoritative {state} owner.");
}
