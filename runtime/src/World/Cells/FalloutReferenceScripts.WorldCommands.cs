using System.Globalization;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.World.Cells;

// Item, ownership, trigger and engine-internal object commands. Each reached
// name either changes its existing owner's state or completes with a visible
// deferral; malformed arguments still stop the script.
internal sealed partial class FalloutReferenceScripts
{
    private sealed record WorldCommandContext(string Command, int PathLength, FalloutFormKey Target,
        FalloutScriptBindings Bindings, IReadOnlyList<string> Arguments, Func<string, double> Number,
        Func<string, FalloutFormKey> Reference);

    private static bool Flag(double value, string command) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException($"{command} flag is not zero or one."),
    };

    private static void RequireArguments(WorldCommandContext context, int minimum, int maximum)
    {
        if (context.Arguments.Count < minimum || context.Arguments.Count > maximum)
            throw new InvalidDataException($"{context.Command} has an invalid argument count ({context.Arguments.Count}).");
    }

    // A compiled form name, or a form-valued variable.
    private FalloutFormKey FormArgument(WorldCommandContext context, string token)
    {
        if (context.Bindings.TryForm(token) is { } bound) return bound.FormKey;
        var value = context.Number(token);
        if (!double.IsFinite(value) || value <= 0 || value > uint.MaxValue || value != Math.Truncate(value))
            throw new InvalidDataException($"{context.Command} form argument has no valid identity.");
        return records.RuntimeFormKey((uint)value);
    }

    private FalloutInventoryCommands WorldInventory(string command) =>
        host.Inventory ?? throw new NotSupportedException($"{command} has no shared inventory owner.");

    // Statement and function forms (set ref to X.PlaceLeveledActorAtMe ...)
    // share this deferral; no runtime actor-spawning owner exists yet.
    private static FalloutScriptFunction PlaceLeveledActorFunction(Func<FalloutFormKey> target) => FalloutScriptFunction.Typed(
        [FalloutScriptArgumentKind.Value, FalloutScriptArgumentKind.OptionalNumber], arguments =>
        {
            if (arguments.Count is < 1 or > 2) throw new InvalidDataException("PlaceLeveledActorAtMe requires a leveled actor and optional level modifier.");
            _ = target();
            DeferPlaceLeveledActor();
            return FalloutScriptValue.Form(0);
        });

    private static void DeferPlaceLeveledActor() =>
        FalloutScriptCommandDeferral.Warn("placeleveledactoratme", "runtime-actor-spawning-has-no-owner;returns-no-reference");

    private bool TryWorldCommand(string operation, WorldCommandContext context)
    {
        var arguments = context.Arguments;
        switch (operation)
        {
            case "entertrigger":
                RequireArguments(context, 1, 1);
                var entering = context.Reference(arguments[0]);
                _ = world.Get(context.Target);
                if (world.IsResident(context.Target)) world.ForcedTriggers.Enter(context.Target, entering);
                else FalloutScriptCommandDeferral.Warn(operation, "unloaded-trigger-receives-no-event");
                return true;
            case "additemhealthpercent":
                RequireArguments(context, 3, 4);
                var count = context.Number(arguments[1]);
                if (count <= 0 || count > int.MaxValue || count != Math.Truncate(count))
                    throw new InvalidDataException("AddItemHealthPercent count must be a positive integer.");
                var health = context.Number(arguments[2]);
                if (!double.IsFinite(health) || health < 0)
                    throw new InvalidDataException("AddItemHealthPercent health must be a finite nonnegative fraction.");
                WorldInventory(context.Command).Execute(new(FalloutInventoryCommandKind.Add, context.Target,
                    Item: context.Bindings.Form(arguments[0]).FormKey, Count: (int)count,
                    Silent: arguments.Count == 4 && Flag(context.Number(arguments[3]), context.Command),
                    Condition: (float)Math.Min(health, 1)));
                return true;
            case "removealltypeditems":
                RequireArguments(context, 3, 5);
                // The container is optional; a four-argument form starts with
                // it unless that first token is a numeric flag literal.
                var hasContainer = arguments.Count == 5 || arguments.Count == 4 &&
                    !double.TryParse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                var offset = hasContainer ? 1 : 0;
                var typeCode = context.Number(arguments[offset + 2]);
                if (!double.IsFinite(typeCode) || typeCode < 0 || typeCode > byte.MaxValue || typeCode != Math.Truncate(typeCode))
                    throw new InvalidDataException("RemoveAllTypedItems type code is not a form type.");
                var exemptions = arguments.Count == offset + 4 ? FormArgument(context, arguments[offset + 3]) : (FalloutFormKey?)null;
                if (exemptions is { } list && records.GetEffective(list).Signature != "FLST")
                    throw new InvalidDataException("RemoveAllTypedItems exemption list is not FLST.");
                WorldInventory(context.Command).Execute(new(FalloutInventoryCommandKind.RemoveAll, context.Target,
                    Destination: !hasContainer || arguments[0] == "0" ? null : context.Reference(arguments[0]),
                    RetainOwnership: Flag(context.Number(arguments[offset]), context.Command),
                    Silent: Flag(context.Number(arguments[offset + 1]), context.Command),
                    Filter: FalloutInventoryCommands.TypedItemFilter(records, (uint)typeCode, exemptions, world.FormLists)));
                return true;
            case "modweaponhealthperc":
                RequireArguments(context, 1, 1);
                WorldInventory(context.Command).ModWeaponHealthPercentage(context.Target, context.Number(arguments[0]));
                return true;
            case "setcellownership" when context.PathLength == 1:
                RequireArguments(context, 1, 2);
                world.SetCellOwnership(FormArgument(context, arguments[0]),
                    arguments.Count == 2 ? FormArgument(context, arguments[1]) : null);
                return true;
            case "placeleveledactoratme":
                RequireArguments(context, 1, 2);
                _ = world.Get(context.Target);
                DeferPlaceLeveledActor();
                return true;
            // Remaining names have no OpenNV consumer for their engine effect:
            // item barter value overrides, unowned overrides, CELL public flags
            // (no trespass owner), door path cost, navmesh travel exclusion,
            // 3D/animation reset, and "do" (DamageObject's short name, see the
            // retail trap script comment "; do = DamageObject").
            case "setitemvalue" or "setminimaluse" or "do":
                RequireArguments(context, 1, 1);
                _ = context.Number(arguments[0]);
                _ = world.Get(context.Target);
                FalloutScriptCommandDeferral.Warn(operation, "engine-effect-has-no-runtime-owner");
                return true;
            case "clearownership" or "reset3dstate":
                RequireArguments(context, 0, 0);
                _ = world.Get(context.Target);
                FalloutScriptCommandDeferral.Warn(operation, "engine-effect-has-no-runtime-owner");
                return true;
            case "setcellpublicflag" or "setpublic" when context.PathLength == 1:
                RequireArguments(context, 2, 2);
                _ = FormArgument(context, arguments[0]);
                _ = Flag(context.Number(arguments[1]), context.Command);
                FalloutScriptCommandDeferral.Warn(operation, "cell-public-flag-has-no-trespass-owner");
                return true;
            case "enablenavmesh" or "disablenavmesh" when context.PathLength == 1:
                RequireArguments(context, 1, 1);
                FalloutScriptCommandDeferral.Warn(operation, "travel-graph-has-no-navmesh-exclusion-owner");
                return true;
            // TrapUpdate refreshes the native trap object's cached hit state;
            // OpenNV traps have no such cache, so it has nothing to update.
            case "trapupdate":
                RequireArguments(context, 0, 0);
                return true;
            default:
                return false;
        }
    }
}
