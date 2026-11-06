using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;
using Action = OpenNV.Runtime.Content.FalloutPackageStepAction;

namespace OpenNV.Runtime.World.Actors;

// Runs Wander, UseItemAt, Ambush, FleeNotCombat and UseWeapon (and invalid
// packages degraded to Travel) as step lists over the shared movement,
// combat-start and package-event owners. Behaviour without a native owner is
// reported once as a deferral; it never stops the actor's AI.
internal partial class RuntimeNativeNpc
{
    // Fallback location radius (source units) for a zero PLDT radius.
    private const float SteppedDefaultRadius = 20;
    private FalloutSteppedPackage? _stepped;
    private FalloutPackageStepProgress? _steps;
    private FalloutPackageStepSettings? _stepSettings;
    internal bool SteppedPackageUsesItem => _stepped?.Procedure is 8 or 16 &&
        _steps is { } progress && FalloutPackageSteps.Current(_stepped, progress) is FalloutPackageStep.UseItemAt or FalloutPackageStep.UseWeapon;

    private bool CurrentPackageLocationReached => _stepped is { } source && _steps is { } steps
        ? source.Steps.Take(steps.Step).Contains(FalloutPackageStep.Travel) || source.Steps[steps.Step] == FalloutPackageStep.Done
        : _nativeMarkerTravelProgress?.Complete ?? _editorTravelProgress?.Complete ?? true;

    private void BeginSteppedPackage(FalloutPluginRecord package, bool initializing)
    {
        var world = _aiWorld ?? throw new NotSupportedException("Stepped package has no shared reference owner.");
        var source = FalloutSteppedPackage.Read(package);
        var state = world.Get(Appearance.Reference!.Value);
        var retained = initializing && state.PackageMotion is { } motion && motion.Package == package.FormKey ? motion.Steps : null;
        _stepSettings ??= FalloutPackageStepSettings.Read(_aiStack!.NumericSettings);
        _stepped = source;
        _steps = retained ?? FalloutPackageSteps.Start(source, NowHours, null);
        _aiPackage = package;
        _travelProgress?.Cancel();
        if (_packageEvents!.Active?.Form == package.FormKey) { }
        else if (retained is not null) _packageEvents.Restore(_packageIdleSource!, retained.DoneHandled);
        else _packageEvents.Change(_packageIdleSource);
        GD.Print($"OPENNV_NATIVE_STEPPED_PACKAGE_READY reference={Appearance.Reference} package={package.FormKey} " +
            $"procedure={source.Procedure} steps={string.Join(',', source.Steps)} restored={retained is not null} step={_steps.Step}");
    }

    private void ClearSteppedPackage() { _stepped = null; _steps = null; }

    private double NowHours => (_aiClock?.DaysPassed ?? 0) * 24.0;

    private void AdvanceSteppedPackage(double delta)
    {
        if (_stepped is not { } source || _steps is not { } progress || Combat is null || _aiError is not null ||
            !Combat.PackageMovementReady || _conversationTarget is not null) return;
        if (Combat.OwnsPose)
        {
            // Combat owns the body; the package resumes at its step afterwards.
            if (source.ContinueDuringCombat)
                FalloutPackageDeferral.Warn("continue-during-combat", "Stepped packages with Continue During Combat are suspended by combat; package-driven combat steps are unowned.");
            return;
        }
        var package = _aiPackage!;
        try
        {
            if (progress.Anchor is null)
                progress = progress with { Anchor = Combat.PackageSourcePoint(GlobalPosition) };
            var observation = Observe(source, progress, delta);
            var (next, action) = FalloutPackageSteps.Advance(source, progress, observation, Combat.PackageRandom, _stepSettings!);
            var leftStep = next.Step != progress.Step;
            Apply(source, package, next, action, delta);
            _steps = next;
            Combat.SetPackageSteps(package, next, clearMove: leftStep);
            if ((action & Action.CompletePackage) != 0) _packageEvents!.Complete();
            if ((action & Action.ReevaluatePackages) != 0) { _aiPollRemaining = 0; _aiQuestRevision = -1; }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or InvalidOperationException or IOException)
        {
            Combat.StopPackageMotion(); _aiError = error.Message; _failedPackage = package.FormKey;
            GD.PushError($"OPENNV_NATIVE_AI_DIVERGENCE reference={Appearance.Reference}: {error.Message}");
        }
    }

    // Package location centre and radius in source units, or null when it is
    // outside this actor's navigation region (the step then holds in place).
    private (float[] Centre, float Radius)? SteppedLocation(FalloutPackageLocation? location, FalloutPackageStepProgress progress)
    {
        var world = _aiWorld!;
        var actor = Appearance.Reference!.Value;
        FalloutFormKey? reference = location?.Type switch
        {
            0 => location.Form,
            6 => world.GetLinkedRef(actor),
            _ => null,
        };
        float[] centre;
        FalloutFormKey cell;
        if (reference is { } placed)
        {
            var placement = world.Placement(placed);
            (centre, cell) = (placement.Position, placement.Cell);
        }
        else if (location?.Type == 3)
        {
            var editor = world.EditorPlacement(actor);
            (centre, cell) = (editor.Position, editor.Cell);
        }
        else
        {
            if (location is { Type: not (1 or 2 or 6) })
                FalloutPackageDeferral.Warn($"stepped-location:{location.Type}", $"Package location type {location.Type} has no resolver; the actor's start position is used.");
            if (location is { Type: 1, Form: { } inCell } && inCell != world.Placement(actor).Cell) return null;
            (centre, cell) = (progress.Anchor!, world.Placement(actor).Cell);
        }
        if (world.TravelRegion(cell) != world.TravelRegion(world.Placement(actor).Cell))
        {
            FalloutPackageDeferral.Warn("stepped-other-region", "Stepped package location is outside the actor's navigation region; the actor holds in place.");
            return null;
        }
        return (centre, location is { Radius: > 0 } ? location.Radius : SteppedDefaultRadius);
    }

    private static float Distance(IReadOnlyList<float> left, IReadOnlyList<float> right) =>
        MathF.Sqrt(Enumerable.Range(0, 3).Sum(index => (left[index] - right[index]) * (left[index] - right[index])));

    private FalloutPackageStepObservation Observe(FalloutSteppedPackage source, FalloutPackageStepProgress progress, double delta)
    {
        var here = Combat!.PackageSourcePoint(GlobalPosition);
        var location = SteppedLocation(source.Location, progress);
        var move = Combat.PackageMoveRequest(source.Form);
        var player = Combat.PackagePlayer;
        var playerDistance = player is null ? float.PositiveInfinity : GlobalPosition.DistanceTo(player.GlobalPosition) / Skeleton.UnitsToMetres;
        var observation = new FalloutPackageStepObservation(NowHours, delta)
        {
            AtLocation = location is not { } goal || Distance(here, goal.Centre) <= goal.Radius,
            PlayerDistance = playerDistance,
            Moving = Combat.PackageMoving,
            LegArrived = move?.Arrived == true,
            InCombat = _aiReferenceState?.Engagement is not null,
        };
        return FalloutPackageSteps.Current(source, progress) switch
        {
            FalloutPackageStep.UseWeapon or FalloutPackageStep.UseItemAt => ObserveUse(source, observation),
            FalloutPackageStep.AmbushWait => ObserveAmbush(source, progress, observation, here),
            FalloutPackageStep.FleeNotCombat => ObserveFlee(source, progress, observation, here),
            _ => observation,
        };
    }

    private bool PackageConditionsPass(FalloutPluginRecord package)
    {
        try { return FalloutCondition.AllPass(FalloutCondition.Read(package), EvaluateAiCondition, evaluateRunOn: true); }
        catch (NotSupportedException error)
        {
            // Selection already admitted these conditions; a re-check reaching
            // an unowned function keeps the step running and stays visible.
            FalloutPackageDeferral.Warn($"stepped-condition:{package.FormKey}", error.Message);
            return true;
        }
    }

    private (FalloutFormKey? Reference, float[]? Position, bool Player) PackageTargetPoint(FalloutPackageTarget? target)
    {
        if (target is not { Type: 0 or 3 }) return (null, null, false);
        var reference = target.Type == 3 ? _aiWorld!.GetLinkedRef(Appearance.Reference!.Value) : target.Form;
        if (reference is not { } form) return (null, null, false);
        if (_aiStack!.RuntimeFormId(form) == 0x14)
            return Combat!.PackagePlayer is { } player ? (form, Combat.PackageSourcePoint(player.GlobalPosition), true) : (form, null, true);
        var state = _aiWorld!.Get(form);
        if (state.Deleted || state.DeletePending || !_aiWorld.IsEnabled(form)) return (form, null, false);
        var placement = _aiWorld.Placement(form);
        return _aiWorld.TravelRegion(placement.Cell) == _aiWorld.TravelRegion(_aiWorld.Placement(Appearance.Reference!.Value).Cell)
            ? (form, placement.Position, false) : (form, null, false);
    }

    private FalloutPackageStepObservation ObserveUse(FalloutSteppedPackage source, FalloutPackageStepObservation observation)
    {
        observation = observation with { ConditionsPass = PackageConditionsPass(_aiPackage!) };
        if (source.Procedure == 8)
        {
            var kind = source.Target switch
            {
                { Type: 2 } declared => (int)declared.RawTarget,
                { Type: 0 or 1, Form: { } form } => UseKind(form),
                _ => 0,
            };
            var target = PackageTargetPoint(source.Target);
            return observation with
            {
                UseKind = kind, HasUseTarget = target.Position is not null || source.Target is { Type: 1 or 2 },
                HasUsedForm = source.Target is { Type: 1, Form: not null }, Seated = _sitting == 1,
                ActionIdle = _animation is null,
            };
        }
        var category = source.Target is { Type: 2 } declaredCategory ? (int)declaredCategory.RawTarget : 12;
        var weapon = Combat!.PackageWeapon(source.Target);
        var aim = PackageTargetPoint(source.AttackTarget);
        var zone = source.Zone is null ? null : SteppedLocation(source.Zone, _steps!);
        return observation with
        {
            CategoryAllowed = category is 0 or 12 or 13 or 21 or 22 or 23,
            WeaponsNone = category == 21,
            Equipped = WeaponDrawn,
            EquippedMatches = weapon.Has,
            HasMatchingWeapon = weapon.Has,
            ClipRounds = weapon.Clip,
            Melee = weapon.Melee,
            WeaponOut = WeaponDrawn || category == 21,
            AimSettled = source.AttackTarget is null || aim.Position is not null,
            TargetInZone = zone is null || aim.Position is { } point && Distance(point, zone.Value.Centre) <= zone.Value.Radius,
            ShotsFired = _steppedShotPending ? 1 : 0,
        };
    }

    private int UseKind(FalloutFormKey form)
    {
        var record = _aiStack!.GetEffective(form);
        if (record.Signature is "REFR" or "ACHR" or "ACRE") record = _aiStack.GetEffective(_aiWorld!.Get(form).Base);
        return record.Signature switch
        {
            "ACTI" or "TACT" => 1, "ARMO" => 2, "BOOK" => 3, "CLOT" => 4, "CONT" => 5, "DOOR" => 6, "INGR" => 7,
            "LIGH" => 8, "MISC" => 9, "FLOR" => 10, "FURN" => 11, "WEAP" => 12, "AMMO" => 13, "NPC_" => 14,
            "CREA" => 15, "KEYM" => 16, "ALCH" => 17, _ => 0,
        };
    }

    private FalloutPackageStepObservation ObserveAmbush(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation, float[] here)
    {
        FalloutPackageDeferral.Warn("ambush-player-trigger",
            "Ambush player trigger uses the shared hostile-detection combat start; fAmbushOverRideRadiusforPlayerDetection and sneak ambush detection modifiers are unowned.");
        var wait = SteppedLocation(source.Location, progress);
        var victim = FalloutAmbushVictim.None;
        var inZone = false;
        if (source.Target is { } target)
        {
            if (target.Type is not (0 or 3))
                FalloutPackageDeferral.Warn("ambush-object-victim", "Ambush object-ID victims have no find owner; the victim stays unresolved.");
            var point = PackageTargetPoint(target);
            var state = point.Reference is { } reference && !point.Player ? _aiWorld!.Get(reference) : null;
            victim = point.Reference is null ? FalloutAmbushVictim.Unresolved
                : state is { Deleted: true } or { DeletePending: true } ? FalloutAmbushVictim.Deleted
                : point.Position is null ? FalloutAmbushVictim.Unresolved
                : !point.Player && _aiStack!.GetEffective(state!.Base).Signature is not ("NPC_" or "CREA") ? FalloutAmbushVictim.Unresolved
                : !point.Player && _aiWorld!.IsDead(point.Reference.Value) ? FalloutAmbushVictim.DeadActor
                : FalloutAmbushVictim.Alive;
            var zone = SteppedLocation(source.Zone ?? source.Location, progress);
            inZone = point.Position is { } position && zone is { } area && Distance(position, area.Centre) <= area.Radius &&
                !(point.Player && NpcDialogueActive?.Invoke() == true);
        }
        return observation with
        {
            Victim = victim,
            VictimInZone = inZone,
            LeashExceeded = wait is { } spot && Distance(here, spot.Centre) > spot.Radius + FalloutPackageSteps.AmbushLeashSlack,
        };
    }

    private FalloutPackageStepObservation ObserveFlee(FalloutSteppedPackage source, FalloutPackageStepProgress progress,
        FalloutPackageStepObservation observation, float[] here)
    {
        var threat = PackageTargetPoint(source.Target);
        var refuge = source.Location is null ? null : SteppedLocation(source.Location, progress);
        var safe = source.Target?.CountOrDistance ?? -1;
        var inDanger = refuge is { } shelter ? Distance(here, shelter.Centre) > shelter.Radius
            : threat.Position is { } danger && Distance(here, danger) < safe;
        return observation with
        {
            CannotFlee = _sitting != 0 || Combat!.Dead,
            HasThreat = threat.Position is not null,
            HasRefuge = refuge is not null,
            InDanger = inDanger,
        };
    }

    private bool _steppedShotPending;

    private void Apply(FalloutSteppedPackage source, FalloutPluginRecord package, FalloutPackageStepProgress progress,
        Action action, double delta)
    {
        var world = _aiWorld!;
        var actor = Appearance.Reference!.Value;
        var combat = Combat!;
        var sneaking = (action & Action.Sneak) != 0 || source.Sneaking;
        var weaponDrawn = source.WeaponDrawn || source.Procedure == 16 && FalloutPackageSteps.Current(source, progress) == FalloutPackageStep.UseWeapon;
        _steppedShotPending = false;
        FalloutWanderProgress? request = null;
        if ((action & Action.MoveToLocation) != 0 && SteppedLocation(source.Location, progress) is { } goal)
            request = combat.PackageMoveRequest(source.Form) is { Arrived: false } current ? current : world.PackageMoveRequest(actor, goal.Centre);
        if ((action & Action.WanderLeg) != 0 && SteppedLocation(source.Location, progress) is { } area)
        {
            try
            {
                request = world.BeginPackageWander(actor, new Vector3(area.Centre[0], area.Centre[1], area.Centre[2]), area.Radius,
                    _aiStack!.NumericSettings, _aiGlobals, Skeleton.UnitsToMetres);
            }
            catch (NotSupportedException error) { FalloutPackageDeferral.Warn("stepped-wander-leg", error.Message); }
        }
        if ((action & Action.MoveToHidingPoint) != 0 && SteppedLocation(source.Location, progress) is { } spot &&
            SteppedLocation(source.Zone, progress) is { } zone)
        {
            // Nearest point toward the ambush zone, kept inside the wait spot.
            var offset = Enumerable.Range(0, 3).Select(index => zone.Centre[index] - spot.Centre[index]).ToArray();
            var length = MathF.Sqrt(offset.Sum(value => value * value));
            var scale = length <= spot.Radius || length == 0 ? 1 : spot.Radius / length;
            request = world.PackageMoveRequest(actor, Enumerable.Range(0, 3).Select(index => spot.Centre[index] + offset[index] * scale).ToArray());
        }
        if ((action & Action.Flee) != 0 && combat.PackageMoveRequest(source.Form) is not { Arrived: false })
        {
            var here = combat.PackageSourcePoint(GlobalPosition);
            if (source.Location is not null && SteppedLocation(source.Location, progress) is { } refuge)
                request = world.PackageMoveRequest(actor, refuge.Centre);
            else if (PackageTargetPoint(source.Target).Position is { } threat)
            {
                var away = Enumerable.Range(0, 3).Select(index => index == 2 ? 0 : here[index] - threat[index]).ToArray();
                var length = Math.Max(1, MathF.Sqrt(away.Sum(value => value * value)));
                var distance = Math.Max(source.Target!.CountOrDistance, 1);
                request = world.PackageMoveRequest(actor, Enumerable.Range(0, 3).Select(index => here[index] + away[index] / length * distance).ToArray());
            }
        }
        var running = source.Running || (action & Action.Flee) != 0;
        request ??= (action & Action.Hold) == 0 ? combat.PackageMoveRequest(source.Form) : null;
        if (request is { Arrived: false } move) combat.AdvancePackageWander(package, move, running, weaponDrawn, delta, sneaking);
        else combat.AdvancePackageMotion(package, GlobalPosition, 0, false, delta, weaponDrawn, requireArrivalHeight: false, sneaking: sneaking);

        if ((action & Action.Aim) != 0 && PackageTargetPoint(source.AttackTarget ?? source.Target).Position is { } aim)
        {
            var direction = combat.PackageNativePoint(aim) - GlobalPosition;
            direction.Y = 0;
            if (direction.LengthSquared() > 1e-6f) combat.FacePackageDirection(direction.Normalized(), delta);
        }
        if ((action & Action.StartCombatWithPlayer) != 0) world.StartCombat(actor, _aiStack!.RuntimeFormKey(0x14));
        if ((action & Action.StartCombatWithVictim) != 0 && PackageTargetPoint(source.Target).Reference is { } victim)
            world.StartCombat(actor, victim);
        if ((action & Action.ResolveVictim) != 0)
            FalloutPackageDeferral.Warn("ambush-victim-search", "Ambush victim re-resolution has no find owner; the authored reference is retried.");
        if ((action & (Action.Equip | Action.Unequip)) != 0)
            FalloutPackageDeferral.Warn("use-weapon-equip", "UseWeapon equip/unequip of a specific weapon has no presented-weapon swap owner; the actor's package weapon is used.");
        if ((action & Action.Reload) != 0)
            FalloutPackageDeferral.Warn("use-weapon-reload", "UseWeapon reload and crouch-to-reload have no NPC package ammunition owner.");
        if ((action & Action.Fire) != 0)
        {
            if (combat.PackageWeapon(source.Target).Melee)
                FalloutPackageDeferral.Warn("use-weapon-melee", "UseWeapon melee attacks have no package attack owner; the weighted attack choice is counted without a strike.");
            else
                FalloutPackageDeferral.Warn("use-weapon-discharge", "UseWeapon ranged shots have no package projectile owner; volleys are counted without a projectile (Always Hit/Do No Damage are inert).");
            _steppedShotPending = true;
        }
        if ((action & Action.WeaponTargetWarning) != 0)
            FalloutPackageDeferral.Warn($"useitem-weapon:{source.Form}", $"UseItem package {source.EditorId} has a weapon target; should be UseWeapon.");
        if ((action & Action.NonWeaponTargetWarning) != 0)
            FalloutPackageDeferral.Warn($"useweapon-nonweapon:{source.Form}", $"UseWeapon package {source.EditorId} has a non-weapon target; should be UseItem.");
        if ((action & (Action.Sit | Action.Activate | Action.PlayUseIdle | Action.ConsumeItem)) != 0)
            FalloutPackageDeferral.Warn("useitem-interaction", "UseItemAt sitting, activation, item-use idles and consumption have no package interaction owner; uses are counted in place.");
    }
}
