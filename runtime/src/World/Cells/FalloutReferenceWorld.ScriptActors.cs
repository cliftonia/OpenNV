using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceInstance
{
    // Bound only while a resident RuntimeNativeDestructible owns this reference.
    internal Action<float>? ScriptObjectDamage { get; set; }
}

// Script-facing actor, alarm and faction commands. Persistent state lives in
// the existing actor-override owner (ghost flag, player-enemy faction flags).
internal sealed partial class FalloutReferenceWorld
{
    private readonly Dictionary<FalloutFormKey, bool> _sourcePlayerEnemy = [];

    // A reached command whose effect needs an absent subsystem continues the
    // script, but reports itself once per process so the gap stays visible.
    internal void RequireScriptReference(FalloutFormKey reference)
    {
        if (reference != _enginePlayer) _ = Get(reference);
    }

    internal void RequireScriptActor(FalloutFormKey reference)
    {
        if (reference != _enginePlayer) _ = Actor(reference);
    }

    internal bool IsGhost(FalloutFormKey reference) => _actorOverrides.GetValueOrDefault(reference)?.Ghost == true;

    internal void SetGhost(FalloutFormKey reference, bool ghost)
    {
        if (reference != _enginePlayer) _ = Actor(reference);
        var current = Overrides(reference);
        _actorOverrides[reference] = current with { Ghost = ghost };
    }

    // Resurrect and ResurrectActor return a dead actor to life with full health
    // and no limb damage. A living actor is unchanged.
    internal void Resurrect(FalloutFormKey reference)
    {
        if (reference == _enginePlayer) throw new NotSupportedException("Player resurrection has no vitals owner.");
        var actor = Actor(reference);
        var health = Health(reference);
        if (actor.Injury?.Dead != true) return;
        // A loaded corpse is owned by its native ragdoll/equipment nodes; living
        // state under them would be captured as an inconsistent body.
        if (actor.CaptureRagdoll is not null || actor.CaptureCorpseEquipment is not null || actor.CaptureCorpseEquipmentForFiniteWait is not null)
            throw new NotSupportedException("Resurrect of a resident corpse has no native revive owner.");
        var restored = health with { Damage = 0 };
        if (restored.Current <= 0) throw new NotSupportedException("Resurrect requires a positive source health baseline.");
        actor.ActorValues["health"] = restored;
        actor.Injury = new(false, null, new Dictionary<byte, float>());
        actor.Ragdoll = null;
        actor.CorpseEquipment = null;
        actor.InterruptedSandboxAdmission = null;
        actor.KnockedDown = false;
        actor.Engagement = null;
        actor.AggroWarning = null;
    }

    internal void RemoveFromAllFactions(FalloutFormKey reference)
    {
        foreach (var (faction, rank) in ActorFactions(reference))
            if (rank >= 0) ChangeFaction(reference, faction, -1, false);
    }

    internal static bool SourcePlayerEnemy(FalloutPluginRecord faction)
    {
        var flags = faction.ReadSubrecords().Single(field => field.Signature == "DATA").Data.Span;
        if (flags.Length is not (1 or 4)) throw new InvalidDataException("Faction flags require one or four source bytes.");
        return (flags[0] & 0x10) != 0;
    }

    internal bool IsPlayerEnemyOfFaction(FalloutFormKey faction)
    {
        var source = FactionSource(faction);
        if (_actorOverrides.GetValueOrDefault(_enginePlayer)?.PlayerEnemyFactions?.FirstOrDefault(item => item.Form == faction) is { } changed)
            return changed.Value != 0;
        if (!_sourcePlayerEnemy.TryGetValue(faction, out var enemy)) _sourcePlayerEnemy.Add(faction, enemy = SourcePlayerEnemy(source));
        return enemy;
    }

    internal void SetPlayerEnemyOfFaction(FalloutFormKey faction, bool enemy)
    {
        var entry = FormOverride(faction, "FACT", enemy ? 1 : 0);
        var current = Overrides(_enginePlayer);
        _actorOverrides[_enginePlayer] = current with
        { PlayerEnemyFactions = (current.PlayerEnemyFactions ?? []).Where(item => item.Form != faction).Append(entry).ToArray() };
    }

    // Only script-set flags change live combat reactions; members of a flagged
    // faction treat the player as an enemy unless they are the player's teammate.
    private uint? PlayerEnemyRelation(FalloutFormKey actor, FalloutFormKey target)
    {
        var flags = _actorOverrides.GetValueOrDefault(_enginePlayer)?.PlayerEnemyFactions;
        if (flags is not { Count: > 0 } || actor == target || actor != _enginePlayer && target != _enginePlayer) return null;
        var other = actor == _enginePlayer ? target : actor;
        if (Actor(other).PlayerTeammate) return null;
        var factions = ActorFactions(other);
        return flags.Any(item => item.Value != 0 && factions.TryGetValue(item.Form, out var rank) && rank >= 0) ? 1u : null;
    }

    // Alarm responders are loaded, enabled, conscious actors that belong to one
    // of the alarm factions (or are the victim). An actor already fighting a
    // different target keeps that fight.
    internal int SendAlarm(FalloutFormKey? victim, FalloutFormKey criminal, IReadOnlyCollection<FalloutFormKey> factions)
    {
        if (criminal != _enginePlayer) _ = Actor(criminal);
        var started = 0;
        foreach (var instance in ResidentInstances.ToArray())
        {
            if (instance.Reference == criminal || records.GetEffective(instance.Base).Signature is not ("NPC_" or "CREA")) continue;
            if (!IsEnabled(instance.Reference) || instance.Injury?.Dead == true || instance.Unconscious || instance.Restrained) continue;
            if (criminal == _enginePlayer && instance.PlayerTeammate) continue;
            var responds = instance.Reference == victim || factions.Count != 0 &&
                ActorFactions(instance.Reference).Any(pair => pair.Value >= 0 && factions.Contains(pair.Key));
            if (!responds || instance.Engagement is not null) continue;
            instance.AggroWarning = null;
            instance.Engagement = new(criminal);
            started++;
        }
        return started;
    }

    internal IReadOnlyCollection<FalloutFormKey> MemberFactions(FalloutFormKey reference) => reference == _enginePlayer ? [] :
        ActorFactions(reference).Where(pair => pair.Value >= 0).Select(pair => pair.Key).ToArray();

    // Without a perception owner, detection is the actor's own combat or warning
    // target, or a target within the source sneak detection distance.
    internal bool DetectsTarget(FalloutFormKey caller, FalloutFormKey target, Func<FalloutFormKey, FalloutFormKey, float>? distance)
    {
        if (caller == _enginePlayer) throw new NotSupportedException("Player GetDetected has no perception owner.");
        var actor = Actor(caller);
        if (target != _enginePlayer) _ = Get(target);
        if (caller == target || !IsEnabled(caller) || actor.Injury?.Dead == true || actor.Unconscious) return false;
        if (target != _enginePlayer && !IsEnabled(target)) return false;
        if (actor.Engagement?.Target == target || actor.AggroWarning?.Target == target) return true;
        if (distance is null) return false;
        var range = FalloutGameSettingFloats.Read(records, "fSneakMaxDistance");
        if (!float.IsFinite(range) || range <= 0) throw new InvalidDataException("Detection distance setting is invalid.");
        return distance(caller, target) <= range;
    }

    internal void DamageObject(FalloutFormKey reference, float amount)
    {
        if (!float.IsFinite(amount) || amount < 0) throw new InvalidDataException("DamageObject requires a finite nonnegative amount.");
        var instance = Get(reference);
        if (FalloutDestructible.Read(records, instance.Base) is not { } source) return;
        if (instance.ScriptObjectDamage is { } resident) { resident(amount); return; }
        // Unloaded objects advance retained health and stage only. Cold
        // attachment selects the reached model without replaying blasts.
        var previous = instance.Destruction ?? FalloutDestructionState.Initial(source);
        var (next, _) = previous.Damage(source, amount, null);
        instance.Destruction = next;
        if (!ReferenceEquals(previous, next)) instance.ObjectHealthPresent = true;
        instance.Destroyed |= next.Destroyed;
        if (next.Disabled) instance.Enabled = false;
    }

    internal void ClearDestruction(FalloutFormKey reference)
    {
        var instance = Get(reference);
        if (FalloutDestructible.Read(records, instance.Base) is not { } source || instance.Destruction is null) return;
        // The resident stage owner keeps its replaced model; resetting state under
        // it would desynchronize model stage and health.
        if (instance.ScriptObjectDamage is not null)
        {
            FalloutScriptCommandDeferral.Warn("cleardestruction", "resident-destruction-model-has-no-reset-owner");
            return;
        }
        if (instance.Destruction.Destroyed) instance.Destroyed = false;
        instance.Destruction = FalloutDestructionState.Initial(source);
    }
}
