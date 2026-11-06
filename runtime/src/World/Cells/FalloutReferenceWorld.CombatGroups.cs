using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// Allies fighting together and the enemies they fight, as seen by conditions.
internal sealed record FalloutCombatGroup(IReadOnlySet<FalloutFormKey> Members, IReadOnlySet<FalloutFormKey> Targets);

// Combat groups are derived from live engagements rather than stored: every
// resident, enabled, living actor's engagement links it to its target. The
// side of that graph holding an actor is its group; the other side is its
// target list. The player joins a side only through engagements naming it.
internal sealed partial class FalloutReferenceWorld
{
    private static readonly Comparer<FalloutFormKey> KeyOrder =
        Comparer<FalloutFormKey>.Create((left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));

    // Run-on Combat Target: the actor's current engagement target. Non-actors
    // and actors outside combat have none.
    internal FalloutFormKey? CombatTarget(FalloutFormKey primary)
    {
        if (primary == _enginePlayer) throw new NotSupportedException("Player combat target has no combat-target owner.");
        if (records.GetEffective(primary).Signature is not ("ACHR" or "ACRE")) return null;
        var actor = Actor(primary);
        return IsEnabled(primary) && actor.Injury?.Dead != true ? actor.Engagement?.Target : null;
    }

    internal FalloutCombatGroup? CombatGroup(FalloutFormKey actor) => CombatGroups().GetValueOrDefault(actor);

    // One pass over the live engagements. Each connected component is walked
    // from its ordinally smallest reference along sorted links, so an odd
    // cycle still gives every member one consistent group.
    private Dictionary<FalloutFormKey, FalloutCombatGroup> CombatGroups()
    {
        var links = new SortedDictionary<FalloutFormKey, SortedSet<FalloutFormKey>>(KeyOrder);
        void Link(FalloutFormKey from, FalloutFormKey to)
        {
            if (!links.TryGetValue(from, out var set)) links.Add(from, set = new(KeyOrder));
            set.Add(to);
        }
        foreach (var instance in ResidentInstances)
        {
            if (instance.Engagement is not { } engagement || !IsEnabled(instance.Reference) || instance.Injury?.Dead == true) continue;
            var target = engagement.Target;
            if (target == instance.Reference || target != _enginePlayer && (!IsEnabled(target) || IsDead(target))) continue;
            Link(instance.Reference, target);
            Link(target, instance.Reference);
        }
        var groups = new Dictionary<FalloutFormKey, FalloutCombatGroup>();
        foreach (var root in links.Keys)
        {
            if (groups.ContainsKey(root)) continue;
            var side = new Dictionary<FalloutFormKey, bool> { [root] = true };
            var pending = new Queue<FalloutFormKey>([root]);
            while (pending.TryDequeue(out var current))
                foreach (var next in links[current])
                    if (side.TryAdd(next, !side[current])) pending.Enqueue(next);
            var first = side.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet();
            var second = side.Where(pair => !pair.Value).Select(pair => pair.Key).ToHashSet();
            FalloutCombatGroup firstGroup = new(first, second), secondGroup = new(second, first);
            foreach (var (member, onFirst) in side) groups.Add(member, onFirst ? firstGroup : secondGroup);
        }
        return groups;
    }

    // GetShouldAttack: 100 when the subject would take the target-acquisition
    // hostile stance toward the actor, unless it would rather side with one of
    // that actor's current enemies.
    internal float ShouldAttack(FalloutFormKey subject, FalloutFormKey actor)
    {
        if (subject == _enginePlayer) throw new NotSupportedException("Player attack disposition has no AI threat owner.");
        if (!IsPlacedActor(subject) || !IsPlacedActor(actor)) return 0;
        if (IsInCombat(actor, PlayerInCombat))
        {
            var groups = CombatGroups();
            if (groups.GetValueOrDefault(actor) is { } group &&
                group.Targets.Any(enemy => enemy != subject && WouldSideWith(subject, enemy, groups[enemy]))) return 0;
        }
        return Hostile(subject, actor) ? 100 : 0;
    }

    private bool WouldSideWith(FalloutFormKey subject, FalloutFormKey enemy, FalloutCombatGroup enemyGroup) =>
        Friendly(subject, enemy) &&
        !enemyGroup.Targets.Any(other => other != subject && Friendly(subject, other)) &&
        !enemyGroup.Members.Any(member => member != subject && Hostile(subject, member));

    private bool IsPlacedActor(FalloutFormKey reference) => reference == _enginePlayer ||
        records.GetEffective(reference).Signature is "ACHR" or "ACRE";

    // Faction combat reactions: neutral, enemy, ally, friend.
    private bool Friendly(FalloutFormKey actor, FalloutFormKey other) => ActorRelation(actor, other) >= 2;
    private bool Hostile(FalloutFormKey actor, FalloutFormKey other) => ActorThreat(actor).Hostile(ActorRelation(actor, other));
}
