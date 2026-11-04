using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal static class FalloutScriptPosition
{
    internal static bool Supports(string name) => name is "setpos" or "setangle" or "rotate";
    internal static FalloutScriptFunction? Function(string name, Func<FalloutReferenceWorld> world,
        Func<FalloutFormKey> target) => !Supports(name) ? null : new(
        [FalloutScriptArgumentKind.Identifier, FalloutScriptArgumentKind.Number], arguments =>
        {
            var command = name switch { "setpos" => "SetPos", "setangle" => "SetAngle", _ => "Rotate" };
            var axis = arguments[0].Identifier?.ToLowerInvariant() switch
            {
                "x" => 0,
                "y" => 1,
                "z" => 2,
                _ => throw new InvalidDataException($"{command} requires an X, Y or Z axis."),
            };
            var value = (float)arguments[1].Number;
            if (!float.IsFinite(value)) throw new InvalidDataException($"{command} requires a finite source value.");
            switch (name)
            {
                case "setpos":
                    world().SetPositionAxis(target(), axis, value);
                    break;
                case "setangle":
                    world().SetAngleAxis(target(), axis, value);
                    break;
                default:
                    // Rotate spins an object continuously at degrees/second
                    // until stopped. OpenNV has no continuous transform owner;
                    // the authored resting transform is kept.
                    _ = target();
                    FalloutScriptCommandDeferral.Warn("rotate", "continuous-rotation-has-no-transform-owner");
                    break;
            }
            return 0;
        });
}
