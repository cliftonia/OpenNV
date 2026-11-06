using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Content;

// What a step asks its actor adapter to do this tick. Several can combine.
[Flags]
internal enum FalloutPackageStepAction
{
    None = 0,
    MoveToLocation = 1 << 0,
    WanderLeg = 1 << 1,
    Hold = 1 << 2,
    Sneak = 1 << 3,
    StartCombatWithPlayer = 1 << 4,
    StartCombatWithVictim = 1 << 5,
    ResolveVictim = 1 << 6,
    MoveToHidingPoint = 1 << 7,
    Flee = 1 << 8,
    Equip = 1 << 9,
    Unequip = 1 << 10,
    Reload = 1 << 11,
    Aim = 1 << 12,
    DrawWeapon = 1 << 13,
    Fire = 1 << 14,
    Sit = 1 << 15,
    Activate = 1 << 16,
    PlayUseIdle = 1 << 17,
    ConsumeItem = 1 << 18,
    WeaponTargetWarning = 1 << 19,
    NonWeaponTargetWarning = 1 << 20,
    ReevaluatePackages = 1 << 21,
    CompletePackage = 1 << 22,
}

internal enum FalloutAmbushVictim { None, Unresolved, Deleted, DeadActor, Alive }

internal enum FalloutMeleeAttack { PowerForward, PowerBackward, PowerLeft, PowerRight, StandardA, StandardB, Sneak }

// Facts the actor adapter observed this tick. Each step reads only its own.
internal sealed record FalloutPackageStepObservation(double NowHours, double DeltaSeconds)
{
    internal bool AtLocation { get; init; }
    internal bool ConditionsPass { get; init; } = true;
    internal float PlayerDistance { get; init; }
    internal bool Moving { get; init; }
    internal bool LegArrived { get; init; }
    // Ambush
    internal bool PlayerTrigger { get; init; }
    internal FalloutAmbushVictim Victim { get; init; }
    internal bool VictimInZone { get; init; }
    internal bool InCombat { get; init; }
    internal bool LeashExceeded { get; init; }
    // FleeNotCombat
    internal bool CannotFlee { get; init; }
    internal bool HasThreat { get; init; }
    internal bool HasRefuge { get; init; }
    internal bool InDanger { get; init; }
    // UseWeapon
    internal bool CategoryAllowed { get; init; } = true;
    internal bool WeaponsNone { get; init; }
    internal bool CanUseWeapons { get; init; } = true;
    internal bool Equipped { get; init; }
    internal bool EquippedMatches { get; init; }
    internal bool HasMatchingWeapon { get; init; }
    internal int ClipRounds { get; init; }
    internal bool NeedReload { get; init; }
    internal bool Reloading { get; init; }
    internal bool ReloadImpossible { get; init; }
    internal bool Attacking { get; init; }
    internal bool Melee { get; init; }
    internal bool WeaponOut { get; init; }
    internal bool AimSettled { get; init; } = true;
    internal bool TargetInZone { get; init; } = true;
    internal bool Blocked { get; init; }
    internal int ShotsFired { get; init; }
    // UseItemAt
    internal int UseKind { get; init; }
    internal bool HasUseTarget { get; init; }
    internal bool HasUsedForm { get; init; }
    internal bool CanUse { get; init; } = true;
    internal bool Seated { get; init; }
    internal bool ActionIdle { get; init; } = true;
}

internal readonly record struct FalloutPackageStepResult(FalloutPackageStepProgress Progress, FalloutPackageStepAction Action);

internal static class FalloutPackageSteps
{
    internal const float AmbushLeashSlack = 50;
    internal const double AmbushRetrySeconds = 10;
    internal const double FireCooldownSeconds = 0.8;

    internal static FalloutPackageStepProgress Start(FalloutSteppedPackage source, double nowHours, float[]? anchor) =>
        new(source.Form, 0, nowHours, anchor);

    internal static FalloutPackageStep Current(FalloutSteppedPackage source, FalloutPackageStepProgress progress) =>
        source.Steps[progress.Step];

    // A relative jump, clamped to the step list. Leaving a step resets its
    // per-step counters; the target-handled mark and anchor stay with the run.
    internal static FalloutPackageStepProgress Jump(FalloutSteppedPackage source, FalloutPackageStepProgress progress, int k) =>
        progress with
        {
            Step = Math.Clamp(progress.Step + k, 0, source.Steps.Count - 1),
            Timer = 0, Cooldown = 0, Shots = 0, ShotTarget = 0, Volleys = 0, Uses = 0, Hidden = false, Seated = false,
            DoneHandled = progress.DoneHandled && progress.Step + k >= source.Steps.Count - 1,
        };

    internal static FalloutPackageStepResult Advance(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation, Func<uint, uint> random, FalloutPackageStepSettings settings)
    {
        if (progress.Package != source.Form) throw new InvalidDataException("Package step progress belongs to another package.");
        progress.Validate();
        // PSDT duration expiry always lands on Done (+3).
        if (source.DurationHours > 0 && Current(source, progress) != FalloutPackageStep.Done &&
            observation.NowHours - progress.StartedHours >= source.DurationHours)
            progress = Jump(source, progress, 3);
        return Current(source, progress) switch
        {
            FalloutPackageStep.Acquire => new(Jump(source, progress, 1), FalloutPackageStepAction.None),
            FalloutPackageStep.Travel => observation.AtLocation
                ? new(Jump(source, progress, 1), FalloutPackageStepAction.Hold)
                : new(progress, FalloutPackageStepAction.MoveToLocation | (source.Sneaking ? FalloutPackageStepAction.Sneak : 0)),
            FalloutPackageStep.Wander => Wander(progress, observation, settings),
            FalloutPackageStep.AmbushWait => Ambush(source, progress, observation),
            FalloutPackageStep.FleeNotCombat => Flee(source, progress, observation),
            FalloutPackageStep.UseItemAt => UseItem(source, progress, observation, random, settings),
            FalloutPackageStep.UseWeapon => UseWeapon(source, progress, observation, random, settings),
            FalloutPackageStep.Done => Done(source, progress, observation),
            _ => throw new InvalidDataException("Package step is unknown."),
        };
    }

    private static FalloutPackageStepResult Done(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation)
    {
        // Duration 0 re-evaluates the stack at once; a timed package idles until its window ends.
        if (!progress.DoneHandled)
            return new(progress with { DoneHandled = true }, FalloutPackageStepAction.CompletePackage | FalloutPackageStepAction.Hold |
                (source.DurationHours == 0 ? FalloutPackageStepAction.ReevaluatePackages : 0));
        // A Travel- or Acquire-first procedure that has left its location travels again.
        if (source.Steps[0] is FalloutPackageStep.Travel or FalloutPackageStep.Acquire && !observation.AtLocation &&
            source.Location is not null)
            return new(Jump(source, progress, -1), FalloutPackageStepAction.Hold);
        return new(progress, FalloutPackageStepAction.Hold);
    }

    // Walk to a point, idle for fAIIdleWaitTime, pick the next point; a leg
    // longer than fAIMaxWanderTime is abandoned. Wander never finishes itself.
    private static FalloutPackageStepResult Wander(FalloutPackageStepProgress progress, FalloutPackageStepObservation observation,
        FalloutPackageStepSettings settings)
    {
        if (progress.Cooldown <= 0 && !observation.LegArrived && progress.Timer <= 0)
            return new(progress with { Cooldown = settings.MaxWanderSeconds }, FalloutPackageStepAction.WanderLeg);
        if (!observation.LegArrived)
        {
            var remaining = progress.Cooldown - observation.DeltaSeconds;
            return remaining <= 0
                ? new(progress with { Cooldown = settings.MaxWanderSeconds }, FalloutPackageStepAction.WanderLeg)
                : new(progress with { Cooldown = remaining }, FalloutPackageStepAction.None);
        }
        if (progress.Cooldown > 0) return new(progress with { Cooldown = 0, Timer = settings.IdleWaitSeconds }, FalloutPackageStepAction.Hold);
        var wait = progress.Timer - observation.DeltaSeconds;
        return wait <= 0
            ? new(progress with { Timer = 0, Cooldown = settings.MaxWanderSeconds }, FalloutPackageStepAction.WanderLeg)
            : new(progress with { Timer = wait }, FalloutPackageStepAction.Hold);
    }

    private static FalloutPackageStepResult Ambush(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation)
    {
        if (observation.PlayerTrigger)
            return new(progress with { Hidden = false }, FalloutPackageStepAction.StartCombatWithPlayer);
        var action = FalloutPackageStepAction.None;
        if (source.Target is not null)
            switch (observation.Victim)
            {
                case FalloutAmbushVictim.Unresolved:
                    var timer = progress.Timer - observation.DeltaSeconds;
                    if (timer <= 0) { action |= FalloutPackageStepAction.ResolveVictim; timer = AmbushRetrySeconds; }
                    progress = progress with { Timer = timer };
                    break;
                case FalloutAmbushVictim.Deleted:
                    return new(Jump(source, progress with { TargetHandled = true }, 2), FalloutPackageStepAction.Hold);
                case FalloutAmbushVictim.DeadActor when !observation.InCombat:
                    return new(progress with { TargetHandled = true }, FalloutPackageStepAction.Hold |
                        (progress.Hidden && source.HideWhileAmbushing ? FalloutPackageStepAction.Sneak : 0));
            }
        if (observation.LeashExceeded) return new(Jump(source, progress, -1), FalloutPackageStepAction.Hold);
        if (source.HideWhileAmbushing)
        {
            if (!progress.Hidden) { progress = progress with { Hidden = true }; action |= FalloutPackageStepAction.MoveToHidingPoint; }
            else action |= FalloutPackageStepAction.Sneak;
        }
        if (observation.Victim == FalloutAmbushVictim.Alive && !progress.TargetHandled && observation.VictimInZone && !observation.InCombat)
            return new(progress with { Hidden = false }, FalloutPackageStepAction.StartCombatWithVictim);
        return new(progress, (action & FalloutPackageStepAction.MoveToHidingPoint) != 0 ? action : action | FalloutPackageStepAction.Hold);
    }

    private static FalloutPackageStepResult Flee(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation)
    {
        if (observation.CannotFlee || !observation.HasThreat && !observation.HasRefuge)
            return new(Jump(source, progress, 1), FalloutPackageStepAction.Hold);
        if (observation.InDanger) return new(progress, FalloutPackageStepAction.Flee);
        if (observation.Moving && (source.MustComplete || source.MustReach))
            return new(Jump(source, progress, 1), FalloutPackageStepAction.Hold);
        return new(progress, FalloutPackageStepAction.Hold);
    }

    internal static bool UsedInPlace(int kind) => kind is 1 or 5 or 6 or 10 or 11 or 14 or 15 or 21 or >= 24 and <= 27;

    private static FalloutPackageStepResult UseItem(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation, Func<uint, uint> random, FalloutPackageStepSettings settings)
    {
        if (observation.PlayerDistance > settings.UseItemDistance) return new(progress, FalloutPackageStepAction.Hold);
        if (!observation.ConditionsPass) return new(Jump(source, progress, -1), FalloutPackageStepAction.Hold);
        if (!observation.HasUseTarget && !observation.CanUse) return new(Jump(source, progress, 1), FalloutPackageStepAction.Hold);
        if (source.SitDown && !progress.Seated)
        {
            if (!observation.Seated) return new(progress, FalloutPackageStepAction.Sit);
            progress = progress with { Seated = true, Timer = 0 };
        }
        var idle = observation.ActionIdle;
        switch (observation.UseKind)
        {
            case 12 or 13 or 21 or 22 or 23:
                return new(progress, FalloutPackageStepAction.Hold | FalloutPackageStepAction.WeaponTargetWarning);
            case 14 or 15:
                return new(progress, FalloutPackageStepAction.Hold);
            case 4 or 19 when !observation.CanUse:
                return new(Jump(source, progress, 2), FalloutPackageStepAction.Hold);
            case 1 or 4 or 19:
                if (progress.Uses >= 1 && idle) return new(Jump(source, progress, 2), FalloutPackageStepAction.Hold);
                if (progress.Uses == 0)
                {
                    progress = progress with { Uses = 1 };
                    return source.OncePerDay && source.DurationHours == 0 && idle
                        ? new(Jump(source, progress, 2), FalloutPackageStepAction.Activate)
                        : new(progress, FalloutPackageStepAction.Activate);
                }
                return new(progress, FalloutPackageStepAction.Hold);
        }
        if (progress.Uses >= 1 && idle && progress.Timer <= 0)
            return new(Jump(source, progress, 2), FalloutPackageStepAction.ConsumeItem | FalloutPackageStepAction.Hold);
        // An object-type target with nothing concrete (retail: furniture)
        // bounces back to Acquire; with Sit Down its net effect is staying seated.
        if (!observation.HasUsedForm && observation.UseKind != 0)
            return source.SitDown ? new(progress, FalloutPackageStepAction.Hold) : new(Jump(source, progress, -2), FalloutPackageStepAction.Hold);
        if (!observation.HasUsedForm) return new(progress, FalloutPackageStepAction.Hold);
        if (progress.Uses == 0 && idle)
            return new(progress with { Uses = 1, Timer = 5 + random(5000) / 1000.0 }, FalloutPackageStepAction.PlayUseIdle);
        return new(progress with { Timer = progress.Timer - observation.DeltaSeconds }, FalloutPackageStepAction.Hold);
    }

    private static FalloutPackageStepResult UseWeapon(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation, Func<uint, uint> random, FalloutPackageStepSettings settings)
    {
        var use = source.Weapon ?? throw new InvalidDataException("UseWeapon step has no PKW3 declaration.");
        if (observation.PlayerDistance > settings.UseWeaponDistance) return new(progress, FalloutPackageStepAction.Hold);
        if (!observation.ConditionsPass) return new(Jump(source, progress, -1), FalloutPackageStepAction.Hold);
        if (!observation.CategoryAllowed) return new(progress, FalloutPackageStepAction.Hold | FalloutPackageStepAction.NonWeaponTargetWarning);
        if (!observation.CanUseWeapons && !observation.WeaponsNone) return new(Jump(source, progress, 2), FalloutPackageStepAction.Hold);
        if (observation.WeaponsNone && observation.Equipped) return new(progress, FalloutPackageStepAction.Unequip);
        if (!observation.WeaponsNone && !observation.EquippedMatches)
            return observation.HasMatchingWeapon
                ? new(progress, FalloutPackageStepAction.Equip)
                : new(Jump(source, progress, 2), FalloutPackageStepAction.Hold);
        progress = progress with { Shots = progress.Shots + observation.ShotsFired };
        if (progress.ShotTarget == 0)
        {
            var target = Math.Max(1, observation.ClipRounds);
            if (use.VolleyFire && Range(use.VolleyMinimum, use.VolleyMaximum, random) is > 0 and var shots) target = shots;
            progress = progress with { ShotTarget = target };
        }
        var action = FalloutPackageStepAction.None;
        if (progress.Shots >= progress.ShotTarget)
        {
            progress = progress with { Volleys = progress.Volleys + 1, Shots = 0, ShotTarget = 0 };
            if (observation.Melee && !use.RepeatFire && progress.Volleys >= use.Bursts)
            {
                progress = progress with { Timer = 0 };
                action |= FalloutPackageStepAction.Unequip;
            }
            else if (use.VolleyFire)
                progress = progress with { Timer = use.PauseMinimum + (use.PauseMaximum - use.PauseMinimum) * (random(10001) / 10000.0) };
        }
        if (use.CrouchToReload && !source.Sneaking)
        {
            if (!observation.Attacking && (observation.NeedReload || observation.Reloading))
            {
                if (observation.ReloadImpossible) return new(Jump(source, progress, 2), action | FalloutPackageStepAction.Hold);
                return new(progress, action | FalloutPackageStepAction.Sneak | FalloutPackageStepAction.Aim |
                    (observation.NeedReload && !observation.Reloading ? FalloutPackageStepAction.Reload : 0));
            }
        }
        else if (!observation.Melee && observation.NeedReload && !observation.Reloading)
        {
            if (observation.ReloadImpossible) return new(Jump(source, progress, 2), action | FalloutPackageStepAction.Hold);
            return new(progress, action | FalloutPackageStepAction.Reload | FalloutPackageStepAction.Aim);
        }
        if (progress.Timer <= 0 && !use.RepeatFire && progress.Volleys >= use.Bursts)
            return new(Jump(source, progress, 2), action | FalloutPackageStepAction.Hold);
        var fire = progress.Timer <= 0 && observation.AimSettled && observation.TargetInZone &&
            !(use.HoldFireWhenBlocked && observation.Blocked);
        if (!observation.WeaponOut) return new(progress, action | FalloutPackageStepAction.DrawWeapon | FalloutPackageStepAction.Aim);
        var cooldown = Math.Max(0, progress.Cooldown - observation.DeltaSeconds);
        if (fire && cooldown <= 0 && !observation.Attacking)
            return new(progress with { Cooldown = FireCooldownSeconds }, action | FalloutPackageStepAction.Fire | FalloutPackageStepAction.Aim);
        return new(progress with { Cooldown = cooldown, Timer = Math.Max(0, progress.Timer - observation.DeltaSeconds) },
            action | FalloutPackageStepAction.Aim);
    }

    private static int Range(int minimum, int maximum, Func<uint, uint> random) =>
        maximum <= minimum ? minimum : minimum + (int)random((uint)(maximum - minimum + 1));

    // Four directional power attacks weigh 10 each, the two standard attacks
    // 30 each. A moving power attack becomes standard A on d100 <= 20, else B.
    // A sneaking actor whose sneak attack is available uses it instead.
    internal static FalloutMeleeAttack ChooseMeleeAttack(Func<uint, uint> random, bool moving, bool sneakAttack)
    {
        if (sneakAttack) return FalloutMeleeAttack.Sneak;
        var roll = random(100);
        var attack = roll switch
        {
            < 10 => FalloutMeleeAttack.PowerForward,
            < 20 => FalloutMeleeAttack.PowerBackward,
            < 30 => FalloutMeleeAttack.PowerLeft,
            < 40 => FalloutMeleeAttack.PowerRight,
            < 70 => FalloutMeleeAttack.StandardA,
            _ => FalloutMeleeAttack.StandardB,
        };
        if (moving && attack <= FalloutMeleeAttack.PowerRight)
            attack = random(100) + 1 <= 20 ? FalloutMeleeAttack.StandardA : FalloutMeleeAttack.StandardB;
        return attack;
    }
}

// Game settings consumed by the step machine, read once from the owned table.
internal sealed record FalloutPackageStepSettings(float UseWeaponDistance, float UseItemDistance, double MaxWanderSeconds,
    double IdleWaitSeconds, float AmbushPlayerRadius)
{
    internal static FalloutPackageStepSettings Read(FalloutNumericGameSettings settings)
    {
        // A setting absent from the owned table keeps the step running on the
        // stated fallback and stays visible as a deferral.
        float Positive(string name, float fallback)
        {
            var value = settings.Get(name);
            if (value >= 0 && double.IsFinite(value)) return (float)value;
            World.Cells.FalloutPackageDeferral.Warn($"package-setting:{name}", $"Game setting {name} is absent; package steps use {fallback}.");
            return fallback;
        }
        return new(Positive("fAIUseWeaponDistance", float.PositiveInfinity),
            Positive("fAIIdleAnimationDistance", float.PositiveInfinity),
            Positive("fAIMaxWanderTime", 30), Positive("fAIIdleWaitTime", 5),
            Positive("fAmbushOverRideRadiusforPlayerDetection", 0));
    }
}
