using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Content.Definitions;
using OpenNV.Runtime.World.Cells;

// Script name resolution: public command aliases, the `Ref. Command` spelling,
// Else trailing text, the condition-function bridge and SetQuestDelay.
internal static class ScriptNameContracts
{
    internal static void Run()
    {
        Aliases();
        Syntax();
        ConditionBridge();
        QuestDelay();
    }

    private static void Aliases()
    {
        ScriptManualSaveContracts.Require(FalloutScriptCommandNames.Canonical("player.GetAV") == "player.GetActorValue" &&
            FalloutScriptCommandNames.Operation("GetQR") == "getquestrunning" &&
            FalloutScriptCommandNames.Operation("getpermav") == "getpermanentactorvalue" &&
            FalloutScriptCommandNames.Operation("player.behemoth") == "isalwayshardcore" &&
            FalloutScriptCommandNames.Canonical("this") == "this" && FalloutScriptCommandNames.Canonical("srm") == "srm" &&
            FalloutScriptCommandNames.Canonical("GetQuestRunning") == "GetQuestRunning",
            "Script alias table lost an alias or bound an ambiguous/reserved short name.");
        var calls = new List<(string Command, string[] Arguments)>();
        var requested = new List<string>();
        double assigned = -1;
        FalloutGameModeProgram.Read("begin GameMode\nset value to GetQR SomeQuest + 1\nif GetQC SomeQuest\nendif\n" +
            "Other.MoveTo Player\nEVP\nend").Execute(_ => 0, (_, value) => assigned = value,
            (command, arguments) => calls.Add((command, arguments.ToArray())), name =>
            {
                requested.Add(name);
                return name is "GetQuestRunning" or "GetQuestCompleted" ? new([FalloutScriptArgumentKind.Identifier], _ => 4) : null;
            });
        ScriptManualSaveContracts.Require(assigned == 5 && requested.Contains("GetQuestCompleted") && !requested.Any(name => name is "GetQR" or "GetQC") &&
            calls is [("Other.MoveToMarker", ["Player"]), ("EvaluatePackage", [])],
            "Statement and expression dispatch did not receive full command names.");
        using var fixture = new ScriptSaveFixture("set saved to GetQuestCompleted SavesQuest + 2\nset suffix to GetQC SavesQuest + 2",
            bindQuest: true);
        ScriptManualSaveContracts.Require(fixture.Scripts.Activate(fixture.Caller, fixture.Player).Error is null && fixture.Value(1) == 2 && fixture.Value(2) == 2,
            "GetQC did not execute as GetQuestCompleted.");
    }

    private static void Syntax()
    {
        ScriptManualSaveContracts.Require(FalloutGameModeProgram.Tokens("VHDSpeechNPC01REF. PlayIdle LooseCheering") is ["VHDSpeechNPC01REF.PlayIdle", "LooseCheering"] &&
            FalloutGameModeProgram.Tokens("Quest.local") is ["Quest.local"] && FalloutGameModeProgram.Tokens("set x to 1.") is ["set", "x", "to", "1."],
            "A receiver with a spaced command did not bind as one qualified command.");
        var calls = new List<string>();
        FalloutGameModeProgram.Read("begin GameMode\nOther. PlayIdle Cheer\nOther. Kill\nend")
            .Execute(_ => 0, (_, _) => { }, (command, _) => calls.Add(command));
        ScriptManualSaveContracts.Require(calls is ["Other.PlayIdle", "Other.KillActor"], "Spaced receiver commands lost their receiver or command.");
        var values = new FalloutScriptValueContext(_ => 0, (_, _) => { });
        var context = new FalloutScriptInspectionContext(values, _ => null, (_, _) => throw new InvalidDataException("Unexpected call."));
        var inspected = FalloutGameModeProgram.Read("begin GameMode\nif 1\nelse (Freed == 0)\nendif\nOther.GetAV Health\nreturn\nend")
            .Inspect(context, _ => { });
        ScriptManualSaveContracts.Require(inspected.All(statement => statement.Ownership != "unbound") && inspected[^2].Operation == "getactorvalue",
            "Else trailing text or an aliased statement was refused by inspection.");
        var refused = FalloutGameModeProgram.Read("begin GameMode\nreturn 1\nend").Inspect(context, _ => { });
        ScriptManualSaveContracts.Require(refused is [{ Ownership: "unbound" }], "Return arguments were admitted with Else's exemption.");
    }

    private static void ConditionBridge()
    {
        ScriptManualSaveContracts.Require(FalloutConditionScriptNames.TryParse("GetIsAlerted", out var alerted) && alerted == FalloutConditionFunction.GetAlerted &&
            !FalloutConditionScriptNames.TryParse("GetAlerted", out _) && !FalloutConditionScriptNames.TryParse("80", out _) &&
            FalloutConditionScriptNames.TryParse("getlevel", out var level) && level == FalloutConditionFunction.GetLevel,
            "Condition script names did not follow the command names.");
        using (var fixture = new ScriptSaveFixture("set saved to Exists Other + 1\nset suffix to Other.GetRestrained + 3"))
        {
            var result = fixture.Scripts.Activate(fixture.Caller, fixture.Player);
            ScriptManualSaveContracts.Require(result.Error is null && fixture.Value(1) == 2 && fixture.Value(2) == 3,
                "Object script did not reach the condition evaluator for bridged functions: " + result.Error);
        }
        using (var fixture = new ScriptSaveFixture("set saved to GetIsAlignment 0 + 1\nset suffix to GetIsAlignment"))
        {
            // GetIsAlignment's integer is required; an omitted one is not read as zero.
            ScriptManualSaveContracts.Require(fixture.Scripts.Activate(fixture.Caller, fixture.Player).Error is not null &&
                fixture.Value(1) == 1 && fixture.Value(2) == 0, "A bridged required integer parameter was treated as optional.");
        }
        ScriptManualSaveContracts.Require(FalloutConditionParameters.For(FalloutConditionFunction.HasPerk) is [FalloutConditionParameter.Form,
            FalloutConditionParameter.OptionalInteger] && FalloutConditionParameters.For(FalloutConditionFunction.GetReputationThreshold) is
            [FalloutConditionParameter.Form, FalloutConditionParameter.Integer], "Bridged integer optionality is not explicit.");
        foreach (var shared in new[] { false, true })
        {
            using var fixture = new ScriptSaveFixture("", "set saved to GetPlayerTeammateCount + Exists Other + 5\n" +
                "set suffix to GetRestrained + 9");
            var quests = new FalloutQuestState(fixture.Records);
            var scripts = new FalloutQuestScripts(fixture.Records, quests, new HashSet<FalloutFormKey>(), new(),
                references: fixture.World, defaultProcessingDelay: 0);
            var executor = new FalloutReferenceScripts(fixture.Records, fixture.World, quests, new((_, _) => false, _ => { }));
            scripts.Host = new((_, _) => throw new InvalidDataException("Unexpected stage"), _ => 0, shared ? executor.ExecuteProgram : null);
            scripts.Advance(0);
            // A quest has no calling reference: the subject query refuses after the subjectless one ran.
            ScriptManualSaveContracts.Require(quests.Variable(fixture.Quest, 1) == 6 && quests.Variable(fixture.Quest, 2) == 0 &&
                scripts.Capture().Instances.Single().Error is not null,
                "Quest script bridged a subjectless query wrongly or invented a calling reference.");
        }
    }

    private static void QuestDelay()
    {
        foreach (var shared in new[] { false, true })
        {
            using var fixture = new ScriptSaveFixture("", "set saved to saved + 1\nSetQuestDelay SavesQuest 2", bindQuest: true);
            FalloutQuestScripts Create(FalloutQuestState quests)
            {
                var scripts = new FalloutQuestScripts(fixture.Records, quests, new HashSet<FalloutFormKey>(), new(),
                    references: fixture.World, defaultProcessingDelay: 5);
                var executor = new FalloutReferenceScripts(fixture.Records, fixture.World, quests,
                    new((_, _) => false, _ => { }, SetQuestDelay: scripts.SetQuestDelay));
                scripts.Host = new((_, _) => throw new InvalidDataException("Unexpected stage"), _ => 0,
                    shared ? executor.ExecuteProgram : null);
                return scripts;
            }
            var quests = new FalloutQuestState(fixture.Records);
            var live = Create(quests);
            live.Advance(5);
            live.Advance(1.5);
            ScriptManualSaveContracts.Require(quests.Variable(fixture.Quest, 1) == 1, "Quest script ran before its replaced two-second interval.");
            live.Advance(0.5);
            ScriptManualSaveContracts.Require(quests.Variable(fixture.Quest, 1) == 2, "Quest script did not run on its replaced two-second interval.");
            var snapshot = JsonSerializer.Deserialize<FalloutQuestScriptsSnapshot>(JsonSerializer.Serialize(live.Capture()))!;
            ScriptManualSaveContracts.Require(snapshot.Instances.Single().Clock?.Interval == 2, "Saved quest clock lost the replaced interval.");
            var cold = Create(new FalloutQuestState(fixture.Records));
            cold.Restore(snapshot);
            ScriptManualSaveContracts.Require(cold.Capture().Instances.Single().Clock?.Interval == 2, "Restored quest clock lost the replaced interval.");
        }
        using (var unbound = new ScriptSaveFixture("set saved to 1\nSetQuestDelay SavesQuest 2\nset suffix to 1", "if 1", bindQuest: true))
        {
            var quests = new FalloutQuestState(unbound.Records);
            var scripts = new FalloutQuestScripts(unbound.Records, quests, new HashSet<FalloutFormKey>(), new(),
                references: unbound.World, defaultProcessingDelay: 5);
            var executor = new FalloutReferenceScripts(unbound.Records, unbound.World, quests,
                new((_, _) => false, _ => { }, SetQuestDelay: scripts.SetQuestDelay));
            ScriptManualSaveContracts.Require(executor.Activate(unbound.Caller, unbound.Player).Error?.Contains("unbound script clock") == true &&
                unbound.Value(1) == 1 && unbound.Value(2) == 0, "SetQuestDelay on an unbound quest script was silently ignored.");
        }
        using var missing = new ScriptSaveFixture("set saved to 1\nSetQuestDelay SavesQuest 2\nset suffix to 1", bindQuest: true);
        ScriptManualSaveContracts.Require(missing.Scripts.Activate(missing.Caller, missing.Player).Error is not null && missing.Value(1) == 1 && missing.Value(2) == 0,
            "SetQuestDelay without a quest clock owner was silently ignored.");
        Console.WriteLine("OPENNV_SCRIPT_NAME_PASS aliases=true fullNameDispatch=true spacedReceiver=true elseTail=true " +
            "conditionBridge=true questSubjectRefused=true setQuestDelay=true unboundQuestRefused=true sharedAndFallback=true persisted=true missingOwnerRefused=true");
    }
}
