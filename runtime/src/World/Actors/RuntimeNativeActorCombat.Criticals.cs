using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.World.Actors;

internal sealed partial class RuntimeNativeActorCombat
{
    private object? _lastCritical;

    // The attack arms are the LeftAttack/RightAttack limbs; an actor without
    // such a limb (many creatures) has nothing there to cripple.
    private (float Left, float Right) AttackArmConditions()
    {
        var parts = _world.BodyParts(_state.Reference).Parts;
        float Arm(int actorValue, string name) => parts.Any(part => part.ActorValue == actorValue && part.HealthPercent > 0)
            ? _world.ActorValue(_state.Reference, name) / 100 : 1;
        return (Arm(27, "LeftAttackCondition"), Arm(28, "RightAttackCondition"));
    }

    private float PlayerCharisma() =>
        (_context?.Player() ?? throw new NotSupportedException("Teammate damage scaling has no resident player.")).CombatActorValue(8);

    // A sneak-attack critical needs the target unaware of the sneaking player.
    // No detection-level owner exists, so the multiplier stays unapplied and visible.
    private bool SneakAttack(FalloutFormKey attacker)
    {
        if (attacker != _records.RuntimeFormKey(0x14) || _state.Engagement?.Target == attacker ||
            _context?.Player() is not { Activity.Sneaking: true }) return false;
        FalloutScriptCommandDeferral.WarnCombat("sneak-attack-critical", "target-detection-level-owner-unbound");
        return false;
    }

    private void ReceiveCritical(FalloutCriticalOutcome critical, bool killed, FalloutFormKey attacker)
    {
        var effect = critical.EffectFor(killed);
        _lastCritical = new { attacker = attacker.ToString(), critical.Damage, critical.SneakAttack, killed, effect = effect?.ToString() };
        GD.Print($"OPENNV_ACTOR_CRITICAL reference={_state.Reference} attacker={attacker} damage={critical.Damage:R} killed={killed} effect={effect}");
        // Critical effects (laser/plasma disintegration scripts) need the actor
        // active-effect owner that also runs CastImmediateOnSelf on NPCs.
        if (effect is not null) FalloutScriptCommandDeferral.WarnCombat("critical-effect", "npc-actor-effect-owner-unbound");
    }
}
