using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.World.Cells;

// GetCasinoWinningsLevel <casino> and SetCasinoWinningsLevel <casino> <level>
// read and replace the shared per-casino winnings level.
internal static class FalloutScriptCasinoCommands
{
    internal static FalloutScriptFunction? Function(string name, FalloutPluginStack records, Func<FalloutCasinoWinnings> owner) => name switch
    {
        "getcasinowinningslevel" => new([FalloutScriptArgumentKind.Value],
            arguments => owner().Level(arguments[0].Value.FormKey(records))) { ReadOnly = true },
        "setcasinowinningslevel" => new([FalloutScriptArgumentKind.Value, FalloutScriptArgumentKind.Number], arguments =>
        {
            var level = arguments[1].Number;
            if (!double.IsFinite(level) || level != Math.Truncate(level))
                throw new InvalidDataException("SetCasinoWinningsLevel requires an integer level.");
            owner().SetLevel(arguments[0].Value.FormKey(records), (int)Math.Clamp(level, int.MinValue, int.MaxValue));
            return 0;
        }),
        _ => null,
    };
}
