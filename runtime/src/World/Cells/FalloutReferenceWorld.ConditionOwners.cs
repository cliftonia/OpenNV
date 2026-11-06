using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

// Live-state answers for CTDA functions whose state already has a world owner.
internal sealed partial class FalloutReferenceWorld
{
    // IsCombatTarget: the caller's current combat engagement names the target.
    internal bool IsCombatTarget(FalloutFormKey caller, FalloutFormKey target)
    {
        if (caller == _enginePlayer) throw new NotSupportedException("Player combat target has no combat-target owner.");
        var actor = Actor(caller);
        return actor.Engagement?.Target is { } current && current == target && actor.Injury?.Dead != true;
    }

    // GetPlayerTeammateCount: living actors currently flagged as teammates.
    internal int PlayerTeammateCount() => _instances.Values.Count(instance =>
        instance.PlayerTeammate && !instance.Deleted && instance.Injury?.Dead != true);

    // Exists: a non-null placed reference that the winning source has not deleted.
    internal bool ReferenceExists(FalloutFormKey reference)
    {
        if (reference == _enginePlayer) return true;
        if (!records.TryGetWinner(reference, out var source) || source.IsDeleted ||
            !FalloutCellSceneReader.IsReferenceType(source.Signature)) return false;
        return !_instances.TryGetValue(reference, out var instance) || !instance.Deleted;
    }

    private Func<bool>? _playerChangingSpace;
    private Func<FalloutWeatherTransition>? _weather;
    private Func<IReadOnlyList<FalloutActiveEffect>>? _playerActiveEffects;

    // The session owns cell-change transitions and the sky.
    internal void BindSessionConditions(Func<bool> playerChangingSpace, Func<FalloutWeatherTransition> weather)
    {
        ArgumentNullException.ThrowIfNull(playerChangingSpace);
        ArgumentNullException.ThrowIfNull(weather);
        _playerChangingSpace = playerChangingSpace;
        _weather = weather;
    }

    // IsPlayerMovingIntoNewSpace: from the start of a door load, fast travel or
    // MoveTo until the destination cell is attached.
    internal bool PlayerMovingIntoNewSpace => (_playerChangingSpace ??
        throw new NotSupportedException("Player space transition has no session owner."))();

    internal FalloutWeatherTransition WeatherTransition => (_weather ??
        throw new NotSupportedException("Weather condition has no sky owner."))();

    internal void BindPlayerActiveEffects(Func<IReadOnlyList<FalloutActiveEffect>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _playerActiveEffects = read;
    }

    // Every active-effect instance on the actor, in any state, with its source item.
    internal IReadOnlyList<FalloutActiveEffect> ActiveEffects(FalloutFormKey actor) => actor == _enginePlayer
        ? (_playerActiveEffects ?? throw new NotSupportedException("Player active effects have no effect owner."))()
        : throw new NotSupportedException("NPC active effects have no unified active-effect owner.");
}
