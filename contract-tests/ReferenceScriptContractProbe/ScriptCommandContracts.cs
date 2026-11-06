using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

// Missing-receiver no-ops, EnterTrigger, ClearOwnership, spell list returns,
// loaded-3D visual gates, AI toggles, script query functions and the saved
// cell image space, all against synthetic records.
internal static class ScriptCommandContracts
{
    private const string Plugin = "ScriptCommands.esm";

    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-script-commands-");
        var previousSink = FalloutScriptCommandDeferral.Sink;
        var warnings = new List<string>();
        try
        {
            FalloutScriptCommandDeferral.Sink = warnings.Add;
            File.WriteAllBytes(Path.Combine(directory.FullName, Plugin), Fixture());
            using var records = FalloutPluginStack.Load(directory.FullName, [Plugin]);
            var cell = FalloutCellSceneReader.Read(records, Key(0x800));
            using var world = new FalloutReferenceWorld(records);
            world.LoadCell(cell);
            var quests = new FalloutQuestState(records);
            var activeImage = Key(0x160);
            var scripts = new FalloutReferenceScripts(records, world, quests, new((_, _) => false,
                effect => { if (effect.Kind != FalloutReferenceEffectKind.ReferenceEnable) throw new InvalidDataException("Script command used an unrelated host effect."); },
                IsImageSpaceActive: form => form == activeImage));

            // Receiver variable holding zero: commands and functions are no-ops.
            var result = scripts.Dispatch(Key(0x906), "GameMode");
            var caller = world.Get(Key(0x906));
            Require(result.Error is null, $"Caller script stopped: {result.Error}");
            // Section 5: an unresolved qualifier statement falls back to the
            // previous receiver; an unresolved expression term is zero; a
            // variable that resolved once keeps its stale form.
            Require(caller.Read(1) == 1, "A null receiver function did not return zero.");
            Require(world.Get(Key(0x904)).EnableRequest is { Enabled: false } && FalloutScriptQualifiers.Count("previous-receiver") > 0,
                "A null qualifier statement did not reuse the previous statement's receiver.");
            Require(caller.Read(9) == 2, "A zero qualifier variable did not keep its stale resolved form.");
            Require(Math.Abs(caller.Read(2) - 90) < 1e-4, "GetAngle did not return the stored rotation in degrees.");
            Require(caller.Read(4) == 0 && caller.Read(5) == 1 && caller.Read(6) == 0 && caller.Read(7) == 1 && caller.Read(8) == 1,
                "GetIsGhost, HasLoaded3D, IsLimbGone, IsDLCInstalled or IsImageSpaceActive returned the wrong value.");

            // EnterTrigger runs the trigger script at once for filtered and
            // unfiltered blocks; self-recursion stops at the nesting limit.
            Require(world.Get(Key(0x903)).Read(1) == 1 && world.Get(Key(0x903)).Read(2) == 1,
                "EnterTrigger did not run OnTriggerEnter player and OnTrigger immediately.");
            Require(world.Get(Key(0x904)).Read(1) == FalloutReferenceScripts.EnterTriggerNestingLimit &&
                world.ForcedTriggers.HasPending(Key(0x904)), "Re-entrant EnterTrigger ignored the nesting limit.");
            var quest = records.GetEffective(Key(0x600));
            // A non-reference qualifier in an expression disables the script form.
            Require(scripts.Dispatch(Key(0x907), "GameMode").Error is null && world.Get(Key(0x907)).Read(2) == 0 &&
                world.IsScriptFormDisabled(Key(0x54)) && scripts.Dispatch(Key(0x907), "GameMode").Blocks == 0,
                "A non-reference expression qualifier did not disable its script form.");
            // An unknown operation still fails visibly through the fallback receiver.
            Require(scripts.Dispatch(Key(0x908), "GameMode").Error?.Contains("MissingOperation", StringComparison.Ordinal) == true,
                "An unknown command on a null receiver vanished silently.");
            // A quest has no implicit receiver: "requires a reference" abandons the run.
            var abandoned = FalloutScriptQualifiers.Count("requires-reference-run-abandoned");
            scripts.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), "rq.Disable\nBoxRef.Disable");
            Require(FalloutScriptQualifiers.Count("requires-reference-run-abandoned") == abandoned + 1 && world.IsEnabled(Key(0x905)),
                "A quest statement without a receiver did not abandon the rest of its run.");
            // A queued EnterTrigger admits OnTriggerEnter and OnTrigger together,
            // deferring to a contact enter admitted in the same frame.
            Require(world.ForcedTriggers.Take(Key(0x904), ["OnTriggerEnter"]).Count == 0 &&
                world.ForcedTriggers.Take(Key(0x904), []) is [{ Name: "OnTriggerEnter" }, { Name: "OnTrigger", TriggerReferences: { Count: 1 } }],
                "A queued EnterTrigger lost OnTrigger or contact precedence.");
            // OnOpen/OnClose are ordinary blocks; queued door and trigger events
            // of an unloading reference are dropped instead of firing late.
            Require(scripts.Dispatch(Key(0x903), "OnOpen") is { Blocks: 1, Error: null } && world.Get(Key(0x903)).Read(8) == 1,
                "OnOpen did not run its block.");
            world.Get(Key(0x903)).PendingDoorEvent = "OnClose";
            world.ForcedTriggers.Enter(Key(0x903), Key(0x900));
            world.UnloadCell(Key(0x800));
            Require(world.Get(Key(0x903)).PendingDoorEvent is null && !world.ForcedTriggers.HasPending(Key(0x903)),
                "Queued events outlived their reference's residency.");
            scripts.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), "TriggerRef.EnterTrigger player");
            Require(world.Get(Key(0x903)).Read(1) == 2, "EnterTrigger did not run an unloaded trigger's script.");
            world.LoadCell(cell);

            // Wrong-kind receivers and global AI toggles.
            warnings.Clear();
            scripts.ExecuteStage(quest, quest.ReadSubrecords().ToArray(), string.Join('\n',
                "BoxRef.SetLevel 5", "DropMe", "ActorRef.SetNoAvoidance 1", "SetAllVisible 1", "SetAllReachable 1",
                "BoxRef.ClearOwnership", "SetCellImageSpace TestCell TestImage"));
            Require(!warnings.Any(line => line.Contains("name=setlevel", StringComparison.Ordinal) || line.Contains("name=dropme", StringComparison.Ordinal) ||
                    line.Contains("name=setallreachable", StringComparison.Ordinal) || line.Contains("name=clearownership", StringComparison.Ordinal)),
                "A wrong-kind receiver or a no-op command reported a deferral.");
            Require(FalloutScriptCommandDeferral.WasReported("setnoavoidance") && FalloutScriptCommandDeferral.WasReported("setallvisible") &&
                FalloutScriptCommandDeferral.WasReported("setcellimagespace"),
                "An unread AI flag or an unpresented cell image space hid its deferral.");
            Require(world.AiToggles is { NoAvoidance: true, AllVisible: true }, "SetNoAvoidance/SetAllVisible did not set their global flags.");

            // ClearOwnership removes the reference's own owner and is saved.
            Require(world.Ownership(Key(0x905)).Owner is null, "ClearOwnership kept the reference owner.");
            using (var cold = new FalloutReferenceWorld(records))
            {
                cold.Restore(JsonSerializer.Deserialize<FalloutReferenceSnapshot[]>(JsonSerializer.Serialize(world.Capture()))!);
                cold.RestoreCellImageSpaces(JsonSerializer.Deserialize<FalloutCellImageSpaceChange[]>(
                    JsonSerializer.Serialize(world.CaptureCellImageSpaces())));
                cold.LoadCell(cell);
                Require(cold.Ownership(Key(0x905)).Owner is null, "Cleared ownership did not survive save restoration.");
                Require(cold.CellImageSpace(Key(0x800))?.Form == Key(0x161), "SetCellImageSpace did not survive save restoration.");
            }
            var cleared = world.Capture().Single(reference => reference.Reference == Key(0x905));
            var legacy = JsonSerializer.Deserialize<FalloutNativeCampaignState>(JsonSerializer.Serialize(new
            {
                Schema = FalloutNativeCampaignSave.BeforeScriptCommandStateSchema,
                References = new[] { cleared },
            }))!;
            Reject(() => FalloutNativeCampaignSave.AdmitPreviousOwnerSchema(legacy));
            Require(FalloutNativeCampaignSave.AdmitPreviousOwnerSchema(legacy with { References = [] }).Schema ==
                FalloutNativeCampaignSave.ExpectedSchema, "The preceding schema was not admitted without new owners.");

            // The presented current cell receives a replaced image space at once.
            FalloutImageSpace? presented = null;
            world.BindCellImageSpace(Key(0x800), image => presented = image);
            Execute(records, world, Key(0x900), "setcellimagespace", true, "TestCell", "TestImage");
            Require(presented?.Form == Key(0x161), "SetCellImageSpace waited for the next cell load.");

            // Spell lists report whether they changed; abilities are not cast.
            var known = Key(0x151);
            Require(!FalloutScriptEffectCommands.ChangeSpell(records, world, Key(0x900), true, known) &&
                FalloutScriptEffectCommands.ChangeSpell(records, world, Key(0x900), false, known) &&
                !FalloutScriptEffectCommands.ChangeSpell(records, world, Key(0x900), false, known) &&
                FalloutScriptEffectCommands.ChangeSpell(records, world, Key(0x900), true, known) &&
                !FalloutScriptEffectCommands.ChangeSpell(records, world, Key(0x905), true, known),
                "AddSpell/RemoveSpell returned the wrong change result.");
            Require(Execute(records, world, Key(0x900), "castimmediateonself", false, "Ability") is null &&
                Execute(records, world, Key(0x900), "castimmediateonself", false, "KnownSpell") is ["KnownSpell"],
                "CastImmediateOnSelf cast an ability or dropped an actor effect.");
            Require(FalloutScriptEffectCommands.DispelledByDispelAll(records, known) &&
                FalloutScriptEffectCommands.DispelledByDispelAll(records, Key(0x153)) &&
                !FalloutScriptEffectCommands.DispelledByDispelAll(records, Key(0x152)),
                "DispelAllSpells selected the wrong spell types.");

            // Visual commands on a loaded actor; unloading drops the transient state.
            var revision = world.PresentationRevision;
            Require(Execute(records, world, Key(0x900), "setactoralpha", false, "1.5") is ["1"] && world.ActorAlpha(Key(0x900)) == 1,
                "SetActorAlpha did not clamp.");
            Require(Execute(records, world, Key(0x900), "setactoralpha", false, "0.25") is ["0.25"] &&
                world.ActorAlpha(Key(0x900)) == .25f && world.PresentationRevision > revision,
                "SetActorAlpha did not publish its value to presentation.");
            Require(Execute(records, world, Key(0x900), "playmagicshadervisuals", false, "Shader") is ["Shader"] &&
                world.MagicShaders(Key(0x900)).SequenceEqual([Key(0x150)]), "PlayMagicShaderVisuals lost its default duration.");
            world.UnloadCell(Key(0x800));
            Require(world.ActorAlpha(Key(0x900)) == 1 && world.MagicShaders(Key(0x900)).Count == 0,
                "Actor alpha or shader instances outlived the actor's loaded 3D.");

            var controls = new FalloutPlayerControlState(true, false, true, true, true, true, true);
            Require(controls.AnyDisabled(0b11) && !controls.AnyDisabled(0b101) && !controls.AnyDisabled(0),
                "GetPlayerControlsDisabled mask selected the wrong controls.");
            Console.WriteLine("OPENNV_SCRIPT_COMMAND_CONTRACT_PASS nullReceiver=true previousReceiver=true staleQualifier=true questAbandon=true " +
                "formDisabled=true unknownVisible=true queuedOnTrigger=true doorEvents=true wrongKindNoOp=true enterTrigger=true nesting=true " +
                "unloadedTrigger=true clearOwnership=true cold=true schema=true cellImageSpace=true spellReturns=true cios=true " +
                "dispelAll=true alpha=true shader=true aiToggles=true queries=true controls=true presentation=unverified");
        }
        finally
        {
            FalloutScriptCommandDeferral.Sink = previousSink;
            directory.Delete(true);
        }
    }

    private static IReadOnlyList<string>? Execute(FalloutPluginStack records, FalloutReferenceWorld world, FalloutFormKey target,
        string operation, bool global, params string[] arguments)
    {
        var script = records.GetEffective(Key(0x600));
        var bindings = new FalloutScriptBindings(records, script, script, script.ReadSubrecords());
        return FalloutScriptEffectCommands.Execute(records, world, target, operation, global, arguments,
            new(bindings.Form, value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), bindings.Reference));
    }

    private static byte[] Fixture()
    {
        var header = new byte[12]; BinaryPrimitives.WriteSingleLittleEndian(header, 1.34f);
        var turned = new byte[24]; BinaryPrimitives.WriteSingleLittleEndian(turned.AsSpan(20), MathF.PI / 2);
        static byte[] Spell(uint id, string name, uint type)
        {
            var spit = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(spit, type);
            return Record("SPEL", id, Field("EDID", Text(name)), Field("SPIT", spit));
        }
        var references = Join(
            Record("ACHR", 0x900, Field("EDID", Text("ActorRef")), Field("NAME", BitConverter.GetBytes(0x110u)), Field("DATA", turned)),
            Placed(0x903, 0x111, "TriggerRef"), Placed(0x904, 0x112, "RecursiveRef"),
            Record("REFR", 0x905, Field("EDID", Text("BoxRef")), Field("NAME", BitConverter.GetBytes(0x113u)),
                Field("XOWN", BitConverter.GetBytes(0x110u)), Field("DATA", new byte[24])),
            Placed(0x906, 0x114, "CallerRef"), Placed(0x907, 0x115, "DisabledRef"), Placed(0x908, 0x116, "UnknownRef"));
        var group = new byte[24 + references.Length]; Encoding.ASCII.GetBytes("GRUP").CopyTo(group, 0);
        UInt(group, 4, (uint)group.Length); UInt(group, 8, 0x800); UInt(group, 12, 6); references.CopyTo(group, 24);
        var formIds = new uint[] { 0x14, 0x900, 0x903, 0x904, 0x905, 0x150, 0x151, 0x152, 0x160, 0x161, 0x800, 0x110 };
        return Join(Record("TES4", 0, Field("HEDR", header)),
            Record("NPC_", 7, Field("ACBS", new byte[24])),
            Record("NPC_", 0x110, Field("EDID", Text("ActorBase")), Field("ACBS", new byte[24]), Field("SPLO", BitConverter.GetBytes(0x151u))),
            Record("ACTI", 0x111, Field("SCRI", BitConverter.GetBytes(0x50u))),
            Record("ACTI", 0x112, Field("SCRI", BitConverter.GetBytes(0x51u))),
            Record("CONT", 0x113, Field("EDID", Text("Box"))),
            Record("ACTI", 0x114, Field("SCRI", BitConverter.GetBytes(0x52u))),
            Record("ACTI", 0x115, Field("SCRI", BitConverter.GetBytes(0x54u))),
            Record("ACTI", 0x116, Field("SCRI", BitConverter.GetBytes(0x55u))),
            Record("EFSH", 0x150, Field("EDID", Text("Shader"))),
            Spell(0x151, "KnownSpell", 0), Spell(0x152, "Ability", 4), Spell(0x153, "Power", 2),
            Record("IMAD", 0x160, Field("EDID", Text("ActiveImage"))),
            Record("IMGS", 0x161, Field("EDID", Text("TestImage")), Field("DNAM", new byte[132])),
            Script(0x50, "begin OnTriggerEnter player\nset prefix to prefix + 1\nend\nbegin OnTrigger\nset suffix to suffix + 1\nend\nbegin OnOpen\nset image to image + 1\nend", formIds),
            Script(0x51, "begin OnTriggerEnter\nset prefix to prefix + 1\nRecursiveRef.EnterTrigger player\nend", formIds),
            Script(0x52, "ref r\nbegin GameMode\nTriggerRef.EnterTrigger player\nRecursiveRef.EnterTrigger player\n" +
                "r.Disable\nset prefix to r.GetDisabled + 1\nset suffix to ActorRef.GetAngle Z\n" +
                "set ghost to ActorRef.GetIsGhost\nset loaded to ActorRef.HasLoaded3D\nset limb to ActorRef.IsLimbGone 0 12\n" +
                "set dlc to IsDLCInstalled \"Script Commands\"\nset image to IsImageSpaceActive ActiveImage\n" +
                "set r to ActorRef\nset stale to r.HasLoaded3D\nset r to 0\nset stale to stale + r.GetIsGhost + r.HasLoaded3D\nend", formIds),
            Script(0x53, "ref rq\nbegin GameMode\nend", formIds, quest: true),
            Script(0x54, "ref rb\nbegin GameMode\nset rb to ActorBase\nset prefix to rb.GetDisabled\nset suffix to 1\nend", formIds),
            Script(0x55, "ref r\nbegin GameMode\nr.MissingOperation\nend", formIds),
            Record("CELL", 0x800, Field("EDID", Text("TestCell")), Field("DATA", [1])), group,
            Record("QUST", 0x600, [Field("EDID", Text("CommandQuest")), Field("SCRI", BitConverter.GetBytes(0x53u)), .. formIds.Select(id => Field("SCRO", BitConverter.GetBytes(id)))]));
    }

    private static byte[] Placed(uint id, uint form, string name) => Record("REFR", id, Field("EDID", Text(name)),
        Field("NAME", BitConverter.GetBytes(form)), Field("DATA", new byte[24]));

    private static byte[] Script(uint id, string source, IEnumerable<uint> references, bool quest = false)
    {
        string[] locals = ["prefix", "suffix", "r", "ghost", "loaded", "limb", "dlc", "image", "stale", "rb", "rq", "qflag"];
        var header = new byte[20]; UInt(header, 12, (uint)locals.Length); header[16] = quest ? (byte)1 : (byte)0;
        return Record("SCPT", id, [Field("SCHR", header), .. locals.Select((name, index) => Local((uint)index + 1, name)).SelectMany(value => value),
            .. references.Select(form => Field("SCRO", BitConverter.GetBytes(form))), Field("SCTX", Text(source))]);
    }

    private static byte[][] Local(uint index, string name)
    {
        var data = new byte[24]; UInt(data, 0, index);
        return [Field("SLSD", data), Field("SCVR", Text(name))];
    }

    private static FalloutFormKey Key(uint id) => new(Plugin, id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("An invalid script command state was accepted.");
    }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text + '\0');
    private static byte[] Join(params byte[][] fields) => fields.SelectMany(value => value).ToArray();
    private static void UInt(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static byte[] Field(string name, byte[] data)
    {
        var result = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(result, 6); return result;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var payload = Join(fields); var result = new byte[24 + payload.Length]; Encoding.ASCII.GetBytes(name).CopyTo(result, 0);
        UInt(result, 4, (uint)payload.Length); UInt(result, 12, id); payload.CopyTo(result, 24); return result;
    }
}
