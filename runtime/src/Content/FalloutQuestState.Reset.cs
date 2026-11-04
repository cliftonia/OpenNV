namespace OpenNV.Runtime.Content;

internal sealed partial class FalloutQuestState
{
    // ResetQuest returns a quest to its winning source declaration: entered
    // stages, completion/failure, objectives, variables and the running flag
    // are rebuilt from QUST data, so stage results can be replayed.
    internal void Reset(FalloutFormKey quest)
    {
        if (stack.GetEffective(quest).Signature != "QUST") throw new InvalidDataException($"ResetQuest target {quest} is not QUST.");
        _states.Remove(quest);
        _ = Require(quest);
        Revision++;
        ProgressRevision++;
    }
}
