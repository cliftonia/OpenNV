using OpenNV.Runtime.Content;
using OpenNV.Runtime.World.Cells;

// Owned-data proof that the malformed main-quest and Lonesome Road terminal
// sources execute on their shipped compiled branch extents.
internal static class OwnedCompiledBranchProbe
{
    internal static void Run(string game)
    {
        RuntimeLiveContentSource.Configure(game, RuntimeLiveContentSource.FalloutNewVegasGame,
            activePlugins: RuntimeLiveContentSource.ResolveVanillaPlugins(game), includeImplicitPlugins: false);
        using var content = RuntimeLiveContentSource.Current!;
        using var records = FalloutPluginStack.Load(content.PluginSources);
        Penthouse(records);
        Temple(records);
        Console.WriteLine("OPENNV_OWNED_COMPILED_BRANCH_PASS penthouseGameMode=true penthouseActivate=true templeNcrTarget=true gameplay=unverified");
    }

    private static IReadOnlyList<FalloutScriptEventProgram> Events(FalloutPluginStack records, FalloutFormKey key, string editorId)
    {
        var script = records.GetEffective(key);
        var fields = script.ReadSubrecords().ToArray();
        Require(script.Signature == "SCPT" && FalloutDialogueTopic.Text(fields.Single(field => field.Signature == "EDID").Data.Span) == editorId,
            $"Owned script {key} is not {editorId}.");
        var source = FalloutDialogueTopic.ScriptText(fields.Single(field => field.Signature == "SCTX").Data.Span);
        try { _ = FalloutGameModeProgram.ReadEvents(source); throw new InvalidOperationException($"{editorId} source is well formed."); }
        catch (InvalidDataException) { }
        var events = FalloutGameModeProgram.ReadEvents(source, FalloutGameModeProgram.CompiledBody(fields));
        Require(events.All(block => block.Program.UsesCompiledBranches), $"{editorId} did not bind its compiled extents.");
        return events;
    }

    private static void Penthouse(FalloutPluginStack records)
    {
        var events = Events(records, new("FalloutNV.esm", 0x1746c1), "Lucky38MrHouseTerminalCodeSCRIPT");
        Require(events.Select(block => block.Event).SequenceEqual(["OnLoad", "GameMode", "OnActivate"]), "Penthouse events changed.");
        var gameMode = events[1].Program;
        var activate = events[2].Program;
        foreach (var (keycard, locked, displayed, expected) in new (int, int, string?, string[])[]
        {
            (1, 2, "VMQYesMan01", []),
            (0, 2, "VMQYesMan01", ["Setobjectivedisplayed VMQYesMan01 55 1", "set bKeycardObjective 1"]),
            // The Legion arm follows a stray EndIf after the taken Else: dead in SCDA.
            (0, 2, "VMQ02", []),
            // The NCR arm is a block-level ElseIf: SCDA evaluates it.
            (0, 2, "VMQ05", ["Setobjectivedisplayed VMQ05 42 1", "set bKeycardObjective 1"]),
            (0, 0, "VMQ05", []),
        })
            Require(Run(gameMode, new() { ["bKeycardObjective"] = keycard }, Functions(locked, displayed, 0, 0)).SequenceEqual(expected),
                $"Penthouse GameMode diverged for keycard={keycard} locked={locked} objective={displayed}.");
        foreach (var (science, locked, chips, displayed, expected) in new (int, int, int, string?, string[])[]
        {
            (80, 0, 0, null, ["Activate"]),
            (50, 2, 0, "VMQ02", ["Setobjectivedisplayed VMQ02 12 1", "Activate"]),
            (50, 2, 0, "VMQYesMan01", ["Setobjectivedisplayed VMQYesMan01 55 1", "Activate"]),
        })
            Require(Run(activate, new(), Functions(locked, displayed, science, chips)).SequenceEqual(expected),
                $"Penthouse OnActivate diverged for science={science} locked={locked} objective={displayed}.");
    }

    private static Func<string, FalloutScriptFunction?> Functions(int locked, string? displayed, int science, int chips) => name =>
        name.ToLowerInvariant() switch
        {
            "getlocked" => new([], _ => locked),
            "getobjectivedisplayed" => new([FalloutScriptArgumentKind.Identifier, FalloutScriptArgumentKind.Number], arguments =>
                arguments[0].Identifier!.Equals(displayed, StringComparison.OrdinalIgnoreCase) ? 1 : 0),
            "getobjectivecompleted" => new([FalloutScriptArgumentKind.Identifier, FalloutScriptArgumentKind.Number], _ => 0),
            "isactionref" => new([FalloutScriptArgumentKind.Identifier], arguments =>
                arguments[0].Identifier!.Equals("player", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
            "player.getav" => new([FalloutScriptArgumentKind.Identifier], _ => science),
            "player.getitemcount" => new([FalloutScriptArgumentKind.Identifier], _ => chips),
            "getbuttonpressed" => new([], _ => 1),
            _ => null,
        };

    private static void Temple(FalloutPluginStack records)
    {
        var events = Events(records, new("LonesomeRoad.esm", 0x009c97), "NVDLC04TempleLaunchTerminalSCRIPT");
        var gameMode = events.Single(block => block.Event.Equals("GameMode", StringComparison.OrdinalIgnoreCase)).Program;
        // The NCR-only launch confirmation is a block-level ElseIf after a stray EndIf.
        var calls = Run(gameMode, new() { ["bRunTimer"] = 0, ["nMessage"] = 5, ["nStage"] = 0, ["fTimer"] = 0 }, Functions(0, null, 0, 0));
        Require(calls.SequenceEqual(["set nButton 1", "set bRunTimer 1", "NVDLC04EDETempleTerminalButtonREF.Playgroup Forward 0",
            "SetStage NVDLC04MQ03b 255", "SetStage NVDLC04MQ04 10", "set NVDLC04MQ03b.nMissileTarget 1",
            "PlaySound NVDLC04QSTUlyssesFire", "SetDestroyed 1"], StringComparer.OrdinalIgnoreCase),
            "Temple NCR-target confirmation diverged: " + string.Join(" | ", calls));
    }

    private static List<string> Run(FalloutGameModeProgram program, Dictionary<string, double> values,
        Func<string, FalloutScriptFunction?> functions)
    {
        var calls = new List<string>();
        program.Execute(name => values.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException(name),
            (name, value) => { values[name] = value; calls.Add($"set {name} {value}"); },
            (name, arguments) => calls.Add(string.Join(" ", new[] { name }.Concat(arguments))), functions);
        return calls;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
