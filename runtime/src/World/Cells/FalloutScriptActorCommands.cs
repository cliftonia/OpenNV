using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using Kind = OpenNV.Runtime.Content.FalloutScriptArgumentKind;

namespace OpenNV.Runtime.World.Cells;

// Actor, combat, AI and faction commands shared by quest, object and dialogue
// result scripts. The subject is the explicit receiver or implicit caller.
internal static class FalloutScriptActorCommands
{
    // Commands whose engine effect is animation, physics, AI-process or UI
    // behavior without a runtime owner. They validate and then continue.
    private static readonly Dictionary<string, (Kind[] Arguments, bool Actor, string Reason)> Deferred = new()
    {
        ["pushactoraway"] = ([Kind.Value, Kind.Number], true, "no-scripted-knockback-physics-owner"),
        ["fireweapon"] = ([Kind.Value], false, "no-scripted-projectile-launch-owner"),
        ["useweapon"] = ([Kind.Value, Kind.Value, Kind.Value, Kind.Number, Kind.OptionalNumber, Kind.OptionalNumber,
            Kind.OptionalNumber], true, "no-scripted-attack-owner"),
        ["setpackduration"] = ([Kind.Number], true, "no-package-duration-override-owner"),
        ["forceflee"] = ([Kind.OptionalValue, Kind.OptionalValue], true, "no-flee-procedure-owner"),
        ["setvatstarget"] = ([Kind.Number], false, "no-vats-target-owner"),
        ["setnoavoidance"] = ([Kind.Number], true, "no-actor-avoidance-owner"),
        ["setactorsai"] = ([Kind.Number], false, "no-actor-ai-enable-owner"),
        ["setforcesneak"] = ([Kind.Number], true, "no-forced-sneak-animation-owner"),
        ["setdisposition"] = ([Kind.Value, Kind.Number], true, "no-disposition-owner"),
        ["setlevel"] = ([Kind.Number, Kind.OptionalNumber, Kind.OptionalNumber, Kind.OptionalNumber], true, "no-actor-level-owner"),
        ["startcannibal"] = ([Kind.OptionalValue], true, "no-cannibal-feeding-animation-owner"),
        ["attachashpile"] = ([Kind.OptionalValue], true, "no-ash-pile-spawner"),
        ["pickidle"] = ([], true, "no-scripted-idle-selection-owner"),
        ["setnpcradio"] = ([Kind.Number, Kind.Value], true, "no-npc-radio-owner"),
        ["setallvisible"] = ([Kind.Number], false, "no-global-detection-override-owner"),
        ["setallreachable"] = ([Kind.Number], false, "no-global-pathing-override-owner"),
        ["setcriticalstage"] = ([Kind.Identifier], true, "no-critical-effect-stage-owner"),
        ["alwaysshowactorsubtitles"] = ([Kind.Number], true, "no-actor-subtitle-policy-owner"),
        ["dropme"] = ([], false, "no-containing-inventory-instance"),
    };

    private static readonly HashSet<string> Owned =
    [
        "setghost", "resurrect", "resurrectactor", "sendassaultalarm", "sendtrespassalarm", "setpcenemyoffaction",
        "clearfactionplayerenemyflag", "removefromallfactions", "getdetected", "damageobject", "do", "cleardestruction",
    ];

    internal static bool Supports(string name) => Owned.Contains(name) || Deferred.ContainsKey(name);

    internal static FalloutScriptFunction? Function(string name, FalloutPluginStack records, Func<FalloutReferenceWorld> world,
        Func<FalloutFormKey> subject, Func<FalloutFormKey, FalloutFormKey, float>? distance = null)
    {
        var player = records.RuntimeFormKey(0x14);
        FalloutFormKey Form(FalloutScriptArgument argument) => argument.Value.Kind == FalloutScriptValueKind.Number
            ? records.RuntimeFormKey(Id(argument.Number)) : argument.Value.FormKey(records);
        FalloutFormKey Faction(FalloutScriptArgument argument)
        {
            var faction = Form(argument);
            return records.GetEffective(faction).Signature == "FACT" ? faction
                : throw new InvalidDataException($"{name} faction argument is not FACT.");
        }
        FalloutFormKey? ActorCaller()
        {
            var caller = subject();
            return caller != player && records.GetEffective(caller).Signature is "ACHR" or "ACRE" ? caller : null;
        }
        FalloutScriptFunction Command(Kind[] kinds, Action<IReadOnlyList<FalloutScriptArgument>> run) =>
            new(kinds, arguments => { run(arguments); return 0; });
        if (Deferred.TryGetValue(name, out var deferred))
            return Command(deferred.Arguments, arguments =>
            {
                if (name != "dropme")
                {
                    if (deferred.Actor) world().RequireScriptActor(subject());
                    else world().RequireScriptReference(subject());
                }
                FalloutScriptCommandDeferral.Warn(name, deferred.Reason);
            });
        return name switch
        {
            "setghost" => Command([Kind.Number], arguments => world().SetGhost(subject(), Flag(arguments[0].Number))),
            // The optional animate flag selects a get-up animation only.
            "resurrect" or "resurrectactor" => Command([Kind.OptionalNumber], arguments =>
            {
                if (arguments.Count == 1) _ = Flag(arguments[0].Number);
                world().Resurrect(subject());
            }),
            // [Victim.]SendAssaultAlarm Criminal [VictimFaction]: the victim and
            // members of the victim faction (or of the victim's factions) respond.
            "sendassaultalarm" => Command([Kind.Value, Kind.OptionalValue], arguments =>
            {
                var references = world();
                var victim = ActorCaller();
                IReadOnlyCollection<FalloutFormKey> factions = arguments.Count == 2 ? new[] { Faction(arguments[1]) } :
                    victim is { } owner ? references.MemberFactions(owner) : Array.Empty<FalloutFormKey>();
                _ = references.SendAlarm(victim, Form(arguments[0]), factions);
            }),
            // Caller.SendTrespassAlarm Criminal: the caller and its faction allies respond.
            "sendtrespassalarm" => Command([Kind.Value], arguments =>
            {
                var references = world();
                var caller = ActorCaller();
                references.RequireScriptReference(subject());
                _ = references.SendAlarm(caller, Form(arguments[0]),
                    caller is { } owner ? references.MemberFactions(owner) : Array.Empty<FalloutFormKey>());
            }),
            "setpcenemyoffaction" => Command([Kind.Value, Kind.OptionalNumber], arguments =>
                world().SetPlayerEnemyOfFaction(Faction(arguments[0]), arguments.Count == 1 || Flag(arguments[1].Number))),
            "clearfactionplayerenemyflag" => Command([Kind.Value], arguments =>
                world().SetPlayerEnemyOfFaction(Faction(arguments[0]), false)),
            "removefromallfactions" => Command([], _ => world().RemoveFromAllFactions(subject())),
            // "do" is DamageObject's short name (retail trap script: "; do = DamageObject").
            "damageobject" or "do" => Command([Kind.Number], arguments =>
            {
                var amount = (float)arguments[0].Number;
                world().DamageObject(subject(), amount);
            }),
            "cleardestruction" => Command([], _ => world().ClearDestruction(subject())),
            "getdetected" => new([Kind.Value], arguments =>
                world().DetectsTarget(subject(), Form(arguments[0]), distance) ? 1 : 0) { ReadOnly = true },
            _ => null,
        };
    }

    private static bool Flag(double value) => FalloutPlayerActorValues.SignedInteger(value) != 0;

    private static uint Id(double value) => double.IsFinite(value) && value > 0 && value <= uint.MaxValue && value == Math.Truncate(value)
        ? (uint)value : throw new InvalidDataException("Script form argument is not a form identity.");
}
