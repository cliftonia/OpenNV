using Function = OpenNV.Runtime.Content.Definitions.FalloutConditionFunction;
using OpenNV.Runtime.Content;

// Owned-data proof for the condition framework: which dialogue conditions
// change under empty reference-parameter substitution, and whether the VATS
// camera settings resolve from the owned installation.
internal static class OwnedConditionSourceProbe
{
    internal static void Run(string game)
    {
        RuntimeLiveContentSource.Configure(game, RuntimeLiveContentSource.FalloutNewVegasGame,
            activePlugins: RuntimeLiveContentSource.ResolveVanillaPlugins(game), includeImplicitPlugins: false);
        using var content = RuntimeLiveContentSource.Current!;
        using var records = FalloutPluginStack.Load(content.PluginSources, loadAllSignatureIndexesForAudit: true, out _);
        foreach (var name in new[] { "fVATSSmartCameraCheckHeight", "fVATSSmartCameraCheckStepDistance", "fVATSSmartCameraCheckStepCount",
            "fDispActorCharBase", "fDispActorCharMult", "fDispTargetCharBase", "fDispTargetCharMult", "fDispTargetKarmaMult",
            "fDispActorRespBase", "fDispActorRespMult", "fDispTargetRaceMult", "fDispTargetFactionMult", "fDispTargetFactionRankBase",
            "fDispTargetFactionRankMult", "fDispTargetWeaponBase", "fDispTargetDiseaseBase", "fDispTargetCharOffset" })
            Console.WriteLine($"OPENNV_CONDITION_SETTING name={name} value={(records.NumericSettings.Get(name) is var value && value != -1 ? value.ToString("R") : "unbound")}");
        var empty = records.EffectiveRecords("INFO")
            .SelectMany(info => FalloutCondition.Read(info).Select(condition => (Info: info, Condition: condition)))
            .Where(row => row.Condition.Argument1 == 0 &&
                (FalloutConditionRuntime.ReferenceParameterFunctions.Contains(row.Condition.Kind) || row.Condition.Kind == Function.GetDisposition))
            .ToArray();
        foreach (var group in empty.GroupBy(row => row.Condition.Kind).OrderBy(group => group.Key))
            Console.WriteLine($"OPENNV_EMPTY_REFERENCE_PARAMETER function={group.Key} infos={group.Select(row => row.Info.FormKey).Distinct().Count()} " +
                $"conditions={group.Count()} substituted={FalloutConditionRuntime.ReferenceParameterFunctions.Contains(group.Key)}");
        var flipped = 0;
        foreach (var (info, condition) in empty.Where(row => row.Condition.Kind is Function.Exists or Function.GetFactionRelation))
        {
            // Unsubstituted, an empty reference reads zero. Substituted, Exists
            // reads a present dialogue participant (1); a faction relation reads
            // one of neutral, enemy, ally or friend (0-3).
            var outcomes = condition.Kind == Function.Exists ? new[] { 1f } : [0f, 1f, 2f, 3f];
            var global = (condition.Flags & 4) != 0;
            var changing = global ? "global-comparison" : string.Join(',', outcomes.Where(value => Passes(condition, value) != Passes(condition, 0)));
            if (changing.Length != 0) flipped++;
            Console.WriteLine($"OPENNV_EMPTY_REFERENCE_FLIP info={info.FormKey} function={condition.Kind} runOn={condition.SubjectRunOn} " +
                $"operator={condition.Flags >> 5} comparison={condition.Comparison:R} flipsAt={(changing.Length == 0 ? "none" : changing)}");
        }
        Console.WriteLine($"OPENNV_OWNED_CONDITION_SOURCE_PASS emptyReferenceConditions={empty.Length} existsOrRelationFlips={flipped}");
    }

    private static bool Passes(FalloutCondition condition, float actual) => (condition.Flags >> 5) switch
    {
        0 => actual == condition.Comparison,
        1 => actual != condition.Comparison,
        2 => actual > condition.Comparison,
        3 => actual >= condition.Comparison,
        4 => actual < condition.Comparison,
        5 => actual <= condition.Comparison,
        _ => throw new InvalidDataException("Unknown CTDA comparison operation."),
    };
}
