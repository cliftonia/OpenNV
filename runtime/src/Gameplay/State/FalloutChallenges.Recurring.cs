using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

internal sealed partial class FalloutChallenges
{
    // RemoveRecurringFromChallenge clears the runtime recurring flag. The
    // current progress is kept; the next completion becomes the final one.
    private bool Recurring(FalloutChallengeDefinition definition) =>
        definition.Recurring && !(_state.GetValueOrDefault(definition.Record.FormKey)?.RecurringRemoved ?? false);

    internal void RemoveRecurring(FalloutFormKey form)
    {
        var definition = Definition(form);
        if (!Recurring(definition)) return;
        _state[form] = State(form) with { RecurringRemoved = true };
    }
}
