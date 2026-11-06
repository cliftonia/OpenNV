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
        ["useweapon"] = ([Kind.Value, Kind.Value, Kind.Value, Kind.Number, Kind.OptionalNumber, Kind.OptionalNumber,
            Kind.OptionalNumber], true, "no-scripted-attack-owner"),
        ["setpackduration"] = ([Kind.Number], true, "no-package-duration-override-owner"),
        ["setactorsai"] = ([Kind.Number], false, "no-actor-ai-enable-owner"),
        ["setforcesneak"] = ([Kind.Number], true, "no-forced-sneak-animation-owner"),
        ["setdisposition"] = ([Kind.Value, Kind.Number], true, "no-disposition-owner"),
        ["setlevel"] = ([Kind.Number, Kind.OptionalNumber, Kind.OptionalNumber, Kind.OptionalNumber], true, "no-actor-level-owner"),
        ["startcannibal"] = ([Kind.OptionalValue], true, "no-cannibal-feeding-animation-owner"),
        ["attachashpile"] = ([Kind.OptionalValue], true, "no-runtime-activator-reference-owner"),
        ["pickidle"] = ([], true, "no-scripted-idle-selection-owner"),
    };

    private static readonly HashSet<string> Owned =
    [
        "setghost", "resurrect", "resurrectactor", "sendassaultalarm", "sendtrespassalarm", "setpcenemyoffaction",
        "clearfactionplayerenemyflag", "removefromallfactions", "getdetected", "damageobject", "do", "cleardestruction",
        "setnpcradio", "alwaysshowactorsubtitles", "pushactoraway", "forceflee", "fireweapon", "setvatstarget",
        "setcriticalstage", "setnoavoidance", "setallvisible", "setallreachable", "dropme",
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
                // A receiver of the wrong kind makes the command a no-op.
                if (deferred.Actor && !world().IsScriptActor(subject())) return;
                if (!deferred.Actor) world().RequireScriptReference(subject());
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
            // Actor.AlwaysShowActorSubtitles Flag: this actor's lines show
            // subtitles regardless of the general subtitle preference.
            "alwaysshowactorsubtitles" => Command([Kind.Number], arguments =>
                world().SetAlwaysShowSubtitles(subject(), Flag(arguments[0].Number))),
            // Actor.SetNPCRadio Flag StationRef: the actor carries a receiver.
            "setnpcradio" => Command([Kind.Number, Kind.Value], arguments =>
                world().SetNpcRadio(subject(), Flag(arguments[0].Number), Form(arguments[1]))),
            // Push markers and the player call this as well as actors.
            "pushactoraway" => Command([Kind.Value, Kind.Number], arguments =>
                world().PushActorAway(subject(), Form(arguments[0]), (float)arguments[1].Number)),
            // Optional cell/reference flee destinations are validated; the flee
            // procedure runs away from the threat rather than to a destination.
            // The player has no flee procedure: a player caller does nothing.
            "forceflee" => Command([Kind.OptionalValue, Kind.OptionalValue], arguments =>
            {
                var references = world();
                foreach (var argument in arguments)
                {
                    var destination = Form(argument);
                    if (records.GetEffective(destination).Signature != "CELL") references.RequireScriptReference(destination);
                }
                if (subject() == player) return;
                if (arguments.Count != 0) FalloutScriptCommandDeferral.Warn("forceflee", "flee-destination-has-no-route-owner");
                references.ForceFlee(subject());
            }),
            // Actor.SetCriticalStage Stage: critical effect scripts record their
            // goo/disintegration progress; a non-actor caller is unchanged.
            "setcriticalstage" => Command([Kind.Identifier], arguments =>
                world().SetCriticalStage(subject(), FalloutCriticalStage.Parse(arguments[0].Identifier!))),
            // Ref.SetVATSTarget Flag overrides a destructible's VATS Targetable flag.
            "setvatstarget" => Command([Kind.Number], arguments =>
            {
                var references = world();
                references.RequireScriptReference(subject());
                references.SetVatsTarget(subject(), Flag(arguments[0].Number));
            }),
            // Ref.FireWeapon WeaponForm requires a WEAP; another form only warns.
            "fireweapon" => Command([Kind.Value], arguments =>
            {
                world().RequireScriptReference(subject());
                if (records.GetEffective(Form(arguments[0])).Signature == "WEAP")
                    FalloutScriptCommandDeferral.Warn("fireweapon", "no-scripted-projectile-launch-owner");
                else FalloutScriptCommandDeferral.Warn("fireweapon-non-weapon", "non-weapon-parameter-does-nothing");
            }),
            // SetNoAvoidance is written with a receiver but changes one global
            // flag; SetAllVisible is the global detection override. Neither is
            // saved, and no steering or visibility owner reads them yet.
            "setnoavoidance" => Command([Kind.Number], arguments =>
            {
                world().AiToggles.NoAvoidance = Flag(arguments[0].Number);
                FalloutScriptCommandDeferral.Warn(name, "no-actor-avoidance-steering-reads-flag");
            }),
            "setallvisible" => Command([Kind.Number], arguments =>
            {
                world().AiToggles.AllVisible = Flag(arguments[0].Number);
                FalloutScriptCommandDeferral.Warn(name, "no-detection-visibility-owner-reads-flag");
            }),
            // The shipping engine reads the flag and does nothing else.
            "setallreachable" => Command([Kind.Number], arguments => _ = Flag(arguments[0].Number)),
            // Outside an inventory item's script there is no containing item: the
            // engine does nothing and succeeds.
            "dropme" => Command([], _ => { }),
            "getdetected" => new([Kind.Value], arguments =>
                world().DetectsTarget(subject(), Form(arguments[0]), distance) ? 1 : 0) { ReadOnly = true },
            _ => null,
        };
    }

    private static bool Flag(double value) => FalloutPlayerActorValues.SignedInteger(value) != 0;

    private static uint Id(double value) => double.IsFinite(value) && value > 0 && value <= uint.MaxValue && value == Math.Truncate(value)
        ? (uint)value : throw new InvalidDataException("Script form argument is not a form identity.");
}
