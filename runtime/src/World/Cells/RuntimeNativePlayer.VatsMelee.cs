using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.World.Cells;

// Melee and unarmed VATS: one whole-actor target per admitted actor within the
// extended melee reach. Execution approaches an out-of-reach target, then uses
// the ordinary melee weapon action and its strike/damage publication.
internal partial class RuntimeNativePlayer
{
    // The engine empty-hands fallback has no inventory item instance.
    private Guid VatsWeaponInstance(FalloutWeaponPresentation weapon)
    {
        if (weapon.IsMeleeWeapon && weapon.WeaponAnimationType == 0 && IntrinsicUnarmed) return Guid.Empty;
        return _presentationInventory!.SelectedWeapon(weapon.Form).Extra.Instance is { } instance && instance != Guid.Empty
            ? instance : throw new InvalidOperationException("VATS equipped weapon has no selected physical instance.");
    }

    private float VatsMeleeRange(FalloutWeaponPresentation weapon) =>
        new FalloutVatsMeleeChance(_presentationRecords!).Range(PlayerMeleeReach(weapon), UnitsToMeters);

    private float VatsMeleeDistance(VatsActor actor) =>
        FalloutActorMeleeSelection.Distance(PlayerMeleeAttacker(), actor.Combat.PlayerMeleeGeometry);

    private IReadOnlyList<FalloutVatsSession.Target> MeasureVatsMeleeTargets()
    {
        var weapon = RequireVatsWeapon();
        var reference = _vatsSelectedActor ?? throw new InvalidOperationException("VATS has no selected-view actor.");
        var records = _presentationRecords!;
        var world = _hitReferences!;
        var owner = new FalloutVatsMeleeChance(records);
        _vatsPreparationCandidates = 1;
        if (!_vatsActors.TryGetValue(reference, out var actor) || !ActorAvailable(actor) ||
            VatsMeleeDistance(actor) > owner.Range(PlayerMeleeReach(weapon), UnitsToMeters))
        { _vatsPreparationSkipped = 1; return []; }
        var torso = world.BodyParts(reference).Parts.SingleOrDefault(part => part.Type == 0) ??
            throw new NotSupportedException("VATS melee target has no source torso part.");
        var source = records.GetEffective(world.Get(reference).Base);
        var cost = owner.ActionPointCost(FalloutVatsWeaponCost.Read(records, weapon.Form)
            .ApplyPerks(_combatPerks?.Invoke() ?? [], VatsPerkCondition));
        var arms = ArmConditions(_vatsVitals!.State);
        var chance = VatsPerkChance(weapon.Form, reference, source.FormKey, torso.ActorValue, owner.Calculate(arms.Left, arms.Right,
            VatsStealthMultiplier(weapon.Form, reference, source.FormKey, torso.ActorValue)));
        var visible = VatsActorLineOfSight(reference);
        _vatsPreparationMeasured = 1;
        _vatsTargetMeasurements.Add(new { reference = reference.ToString(), part = torso.Type, melee = true, visible, chance });
        return [new(reference, torso.Type, world.ActorDisplayName(reference), FalloutGameSettingStrings.Read(records, "sActionPointsAttack"),
            Math.Clamp(chance, 0, 100), cost, visible, source.FormKey, torso.ActorValue)];
    }

    private void ApproachVatsMeleeTarget(FalloutWeaponPresentation weapon, FalloutFormKey reference)
    {
        if (!_vatsActors.TryGetValue(reference, out var actor) || !ActorAvailable(actor)) return;
        var reach = PlayerMeleeReach(weapon);
        var distance = VatsMeleeDistance(actor);
        if (distance <= reach) return;
        var stop = new FalloutVatsMeleeChance(_presentationRecords!).ApproachDistance(reach, weapon.WeaponAnimationType == 0, UnitsToMeters);
        var offset = actor.Node.GlobalPosition - GlobalPosition;
        offset.Y = 0;
        if (offset.IsZeroApprox()) return;
        var travel = Math.Min(distance - stop, offset.Length());
        using var collision = MoveAndCollide(offset.Normalized() * travel);
        GD.Print($"OPENNV_NATIVE_VATS_MELEE_APPROACH reference={reference} distance={distance / UnitsToMeters:0.#} " +
            $"remaining={VatsMeleeDistance(actor) / UnitsToMeters:0.#} reach={reach / UnitsToMeters:0.#} blocked={collision is not null}");
    }

    // A queued success strikes the queued actor's actual body when it is in
    // ordinary reach; a queued miss swings without a contact.
    private (Node Collider, Vector3 Point, Vector3 Normal)? VatsMeleeContact(FalloutWeaponPresentation weapon,
        FalloutVatsSession.Attack attack)
    {
        if (!attack.Success || !_vatsActors.TryGetValue(attack.Target.Reference, out var actor) || !ActorAvailable(actor) ||
            VatsMeleeDistance(actor) > PlayerMeleeReach(weapon)) return null;
        return actor.Combat.PlayerMeleeContact(DetectionOrigin, SelfQueryBodies);
    }
}
