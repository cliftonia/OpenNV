using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceWorld
{
    internal void StartCombat(FalloutFormKey reference, FalloutFormKey target)
    {
        if (records.RuntimeFormId(reference) == 0x14)
            throw new NotSupportedException("Scripted player combat has no player engagement owner.");
        var actor = Actor(reference);
        if (target == reference) throw new InvalidDataException("An actor cannot start combat against itself.");
        if (records.RuntimeFormId(target) != 0x14) _ = Actor(target);
        if (!IsEnabled(reference) || actor.Injury?.Dead == true || actor.Unconscious)
            throw new NotSupportedException("Scripted combat for a disabled, dead or unconscious actor is unbound.");
        if (actor.Engagement is { } current)
        {
            if (current.Target == target) return;
            throw new NotSupportedException("Scripted combat target replacement is unbound.");
        }
        actor.AggroWarning = null;
        actor.Engagement = new(target);
    }

    // ForceFlee puts the actor into its flee procedure away from its current
    // combat target, or the player outside combat, until that flee completes.
    internal void ForceFlee(FalloutFormKey reference)
    {
        if (reference == _enginePlayer) return; // The player has no flee procedure.
        var actor = Actor(reference);
        if (actor.Injury?.Dead == true || actor.Unconscious || !IsEnabled(reference)) return;
        var current = actor.Engagement ?? new(_enginePlayer);
        actor.AggroWarning = null;
        actor.Engagement = (current.Action == "flee" ? current : current.Transition("flee")) with { ForcedFlee = true };
    }

    internal void StopCombatAlarmOnActor(FalloutFormKey target)
    {
        if (records.RuntimeFormId(target) != 0x14) _ = Actor(target);
        foreach (var instance in _instances.Values.Where(instance => instance.Engagement?.Target == target ||
            instance.AggroWarning?.Target == target).ToArray())
        {
            if (instance.Engagement?.Target != target && instance.AggroWarning?.Target != target) continue;
            // The shared fighting flag is authoritative. A resident owner also
            // retires its current motion and publishes its actual combat-end event.
            instance.Engagement = null;
            instance.AggroWarning = null;
            instance.StopCombat?.Invoke();
        }
    }

    // StopCombat ends this actor's own engagement; other fighters keep theirs.
    internal void StopCombat(FalloutFormKey reference)
    {
        if (records.RuntimeFormId(reference) == 0x14) return;
        var instance = Get(reference);
        _ = Actor(reference);
        instance.Engagement = null;
        instance.AggroWarning = null;
        instance.StopCombat?.Invoke();
    }
}
