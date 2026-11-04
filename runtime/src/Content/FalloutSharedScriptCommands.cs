using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Content;

// Global commands that behave identically from quest, object and result
// scripts. Both script executors dispatch them through this one owner.
internal static class FalloutSharedScriptCommands
{
    internal static bool TryExecute(string operation, IReadOnlyList<string> arguments, FalloutQuestState quests,
        Func<FalloutChallenges> challenges, Func<string, FalloutPluginRecord> form)
    {
        void Require(int minimum, int maximum)
        {
            if (arguments.Count < minimum || arguments.Count > maximum)
                throw new InvalidDataException($"{operation} has an invalid argument count ({arguments.Count}).");
        }
        FalloutPluginRecord Typed(string signature)
        {
            var record = form(arguments[0]);
            return record.Signature == signature ? record : throw new InvalidDataException($"{operation} argument is not {signature}.");
        }
        switch (operation)
        {
            case "resetquest":
                Require(1, 1);
                quests.Reset(Typed("QUST").FormKey);
                return true;
            case "removerecurringfromchallenge":
                Require(1, 1);
                challenges().RemoveRecurring(Typed("CHAL").FormKey);
                return true;
            // Engine memory/renderer housekeeping: purging cell buffers and
            // marking a scene as complex change no gameplay state in OpenNV.
            case "purgecellbuffers" or "pcb":
                Require(0, 1);
                return true;
            case "setsceneiscomplex":
                Require(1, 1);
                return true;
            default:
                return false;
        }
    }
}
