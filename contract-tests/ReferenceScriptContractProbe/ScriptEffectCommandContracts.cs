using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

internal static class ScriptEffectCommandContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-script-effects-");
        var previousSink = FalloutScriptCommandDeferral.Sink;
        var warnings = new List<string>();
        try
        {
            FalloutScriptCommandDeferral.Sink = warnings.Add;
            File.WriteAllBytes(Path.Combine(directory.FullName, "Effects.esm"), Join(
                Record("TES4", 0),
                Record("NPC_", 0x7, Field("EDID", Name("Player"))),
                Record("NPC_", 0x150, Field("EDID", Name("SyntheticBase"))),
                Record("SPEL", 0x151, Field("EDID", Name("SyntheticAbility"))),
                Record("EFSH", 0x152, Field("EDID", Name("SyntheticShader"))),
                Record("WTHR", 0x153, Field("EDID", Name("SyntheticWeather"))),
                Record("CELL", 0x154, Field("EDID", Name("SyntheticCell"))),
                Record("IMGS", 0x155, Field("EDID", Name("SyntheticImage")), Field("DNAM", new byte[132])),
                Record("ACHR", 0x156, Field("EDID", Name("SyntheticActor")), Field("NAME", BitConverter.GetBytes(0x150u))),
                Record("SCPT", 0x900, Field("SCRO", BitConverter.GetBytes(0x151u)), Field("SCRO", BitConverter.GetBytes(0x152u)),
                    Field("SCRO", BitConverter.GetBytes(0x153u)), Field("SCRO", BitConverter.GetBytes(0x154u)),
                    Field("SCRO", BitConverter.GetBytes(0x155u)), Field("SCRO", BitConverter.GetBytes(0x156u)))));
            using var records = FalloutPluginStack.Load(directory.FullName, ["Effects.esm"]);
            var script = records.GetEffective(new("Effects.esm", 0x900));
            var bindings = new FalloutScriptBindings(records, script, script, script.ReadSubrecords());
            var resolve = new FalloutScriptEffectResolvers(bindings.Form, value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                bindings.Reference);
            using var world = new FalloutReferenceWorld(records);
            var actor = new FalloutFormKey("Effects.esm", 0x156);
            var spell = new FalloutFormKey("Effects.esm", 0x151);
            IReadOnlyList<string>? Execute(FalloutReferenceWorld target, string operation, params string[] arguments) =>
                FalloutScriptEffectCommands.Execute(records, target, actor, operation, true, arguments, resolve);

            Check(Execute(world, "addspell", "SyntheticAbility") is null && world.ScriptSpell(actor, spell) == true,
                "AddSpell did not retain the actor spell grant.");
            Check(Execute(world, "removespell", "SyntheticAbility") is null && world.ScriptSpell(actor, spell) == false,
                "RemoveSpell did not replace the earlier grant.");
            using var cold = new FalloutReferenceWorld(records);
            cold.RestoreActorOverrides(JsonSerializer.Deserialize<FalloutActorOverrides[]>(JsonSerializer.Serialize(world.CaptureActorOverrides())));
            Check(cold.ScriptSpell(actor, spell) == false, "Script spell change did not survive a cold save.");
            Reject(() => Execute(world, "addspell", "SyntheticShader"));

            _ = Execute(world, "pms", "SyntheticShader", "2");
            Check(world.MagicShaders(actor).SequenceEqual([new FalloutFormKey("Effects.esm", 0x152)]), "PMS did not retain its shader request.");
            _ = Execute(world, "sms", "SyntheticShader");
            Check(world.MagicShaders(actor).Count == 0, "SMS did not stop its shader request.");

            Check(Execute(world, "setweather", "SyntheticWeather", "1") is ["SyntheticWeather", "1"], "SetWeather lost its native arguments.");
            Check(Execute(world, "sgtm", "0.5") is ["0.5"], "SGTM lost its resolved multiplier.");
            Reject(() => Execute(world, "sgtm", "0"));
            Check(Execute(world, "saa", "1.5") is ["1"] && world.ActorAlpha(actor) == 1, "SetActorAlpha did not clamp.");
            Check(Execute(world, "saa", "0.4") is not null && Math.Abs(world.ActorAlpha(actor) - 0.4f) < 1e-6, "SetActorAlpha lost its value.");
            Check(Execute(world, "setrumble", "1", "0.5", "0.1") is ["1", "0.5", "0.1"], "SetRumble lost its motor arguments.");
            Reject(() => Execute(world, "setrumble", "1", "-1", "0.1"));
            Check(Execute(world, "cios", "SyntheticAbility") is ["SyntheticAbility"], "CIOS lost its native spell owner.");
            Reject(() => Execute(world, "cios", "SyntheticWeather"));

            _ = Execute(world, "setcellimagespace", "SyntheticCell", "SyntheticImage");
            Check(world.CellImageSpace(new("Effects.esm", 0x154))?.Form == new FalloutFormKey("Effects.esm", 0x155),
                "SetCellImageSpace did not replace the cell's image space.");

            _ = Execute(world, "triggerscreensplatter", "50", "25", "2.0", "2.0");
            _ = Execute(world, "triggerscreensplatter", "50", "25", "2.0", "2.0");
            Check(Execute(world, "setsecexp", "SyntheticActor", "Infantry", "Neutral") is null, "SetSecuritronExpression requested a native owner.");
            Check(warnings.Count(line => line.Contains("name=triggerscreensplatter", StringComparison.Ordinal)) == 1,
                "Deferred command warning was not emitted exactly once.");
            var native = new List<(FalloutFormKey Target, string Command, string[] Arguments)>();
            var scripts = new FalloutReferenceScripts(records, world, new(records), new((_, _) => false, _ => { },
                Command: (target, _, command, arguments) => native.Add((target, command, arguments.ToArray()))));
            var program = FalloutGameModeProgram.Read("begin GameMode\nSyntheticActor.SAA 0\nsgtm 0.25\nSyntheticActor.CIOS SyntheticAbility\n" +
                "PreloadMagicEffect SyntheticShader\nend");
            Reject(() => scripts.ExecuteProgram(records.GetEffective(actor), script, program, 0));
            program = FalloutGameModeProgram.Read("begin GameMode\nSyntheticActor.SAA 0\nsgtm 0.25\nSyntheticActor.CIOS SyntheticAbility\nend");
            native.Clear();
            scripts.ExecuteProgram(records.GetEffective(actor), script, program, 0);
            Check(native.Count == 3 && native.All(call => call.Target == actor) && native[0] is (_, "saa", ["0"]) &&
                native[1] is (_, "sgtm", ["0.25"]) && native[2] is (_, "cios", ["SyntheticAbility"]),
                "Object-script dispatch did not hand resolved effect remainders to the native owner.");
            Console.WriteLine("OPENNV_SCRIPT_EFFECT_COMMAND_PASS spells=true cold=true shaders=true weather=true clock=true alpha=true " +
                "rumble=true imageSpace=true deferredOnce=true presentation=unverified");
        }
        finally
        {
            FalloutScriptCommandDeferral.Sink = previousSink;
            directory.Delete(true);
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid script effect command was accepted.");
    }
    private static byte[] Name(string value) => Encoding.ASCII.GetBytes(value + "\0");
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Field(string name, byte[] data)
    {
        var bytes = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(bytes, 6); return bytes;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var bytes = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)data.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), id); data.CopyTo(bytes, 24); return bytes;
    }
}
