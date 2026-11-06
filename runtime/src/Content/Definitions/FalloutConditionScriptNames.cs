namespace OpenNV.Runtime.Content.Definitions;

// Script command names of condition functions. A function's identity name is
// its command name except where the script command is spelled differently.
internal static class FalloutConditionScriptNames
{
    private static readonly IReadOnlyDictionary<string, FalloutConditionFunction> Names = Build();

    internal static bool TryParse(string operation, out FalloutConditionFunction function) =>
        Names.TryGetValue(operation, out function);

    private static Dictionary<string, FalloutConditionFunction> Build()
    {
        var names = Enum.GetNames<FalloutConditionFunction>().ToDictionary(name => name, Enum.Parse<FalloutConditionFunction>,
            StringComparer.OrdinalIgnoreCase);
        names.Remove(nameof(FalloutConditionFunction.GetAlerted));
        names.Add("GetIsAlerted", FalloutConditionFunction.GetAlerted);
        return names;
    }
}
