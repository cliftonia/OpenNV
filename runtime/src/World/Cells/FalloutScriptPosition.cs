using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal static class FalloutScriptPosition
{
    internal static bool Supports(string name) => name is "setpos" or "setangle" or "rotate";
    // Rotate turns the reference by degrees/second over the calling frame's
    // elapsed time; authored scripts call it every GameMode frame.
    internal static FalloutScriptFunction? Function(string name, Func<FalloutReferenceWorld> world,
        Func<FalloutFormKey> target, Func<double>? elapsedSeconds = null) => !Supports(name) ? null : new(
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
                    var rotated = target();
                    if (elapsedSeconds is null)
                    {
                        FalloutScriptCommandDeferral.Warn("rotate", "caller-has-no-frame-elapsed-time");
                        break;
                    }
                    world().RotateAxis(rotated, axis, value, elapsedSeconds());
                    break;
            }
            return 0;
        });
}
