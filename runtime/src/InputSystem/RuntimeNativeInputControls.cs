using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.InputSystem;

internal sealed partial class RuntimeNativeInputControls(FalloutInputControls controls, DesktopInputConfiguration configuration,
    Func<int, bool> physicalKeyPressed) : Node
{
    private FalloutControlBinding[]? _bound;
    internal event Action<IReadOnlyList<int>>? Remapped;
    internal static string Action(int control) => "fallout_control_" + control;

    public override void _EnterTree()
    {
        Refresh();
        controls.Changed += Refresh;
        NativeGamepadInput.BoundInput = BoundInput;
        NativeGamepadInput.GameplayActive = GameplayActive;
    }

    // Platforms without pointer capture (iOS) cannot signal gameplay through
    // the mouse mode; the owning player's modal state is authoritative there.
    private bool GameplayActive() => IsInsideTree() && !GetTree().Paused &&
        GetParent() is World.Cells.RuntimeNativePlayer { ModalInput: false };
    public override void _ExitTree()
    {
        controls.Changed -= Refresh;
        if (NativeGamepadInput.BoundInput == BoundInput) NativeGamepadInput.BoundInput = null;
        if (NativeGamepadInput.GameplayActive == GameplayActive) NativeGamepadInput.GameplayActive = null;
    }

    // The keyboard binding when present, otherwise the mouse binding.
    private InputEvent? BoundInput(int control) =>
        new[] { controls.Get((uint)control), controls.Get((uint)control, 1) }
            .Where(code => code != -1).Select(NativeScriptKeys.Create).FirstOrDefault();

    private void Refresh()
    {
        var bindings = controls.Bindings.ToArray();
        // Prepare every changed event before modifying the native map. Unknown
        // physical keys leave the previous owner/map intact and fail visibly.
        var changed = Enumerable.Range(0, FalloutInputControls.Count)
            .Where(index => _bound is null || bindings[index] != _bound[index]).ToArray();
        var events = changed.ToDictionary(index => index, index =>
            new[] { controls.Get((uint)index), controls.Get((uint)index, 1) }
                .Where(code => code != -1).Select(code => (Code: code, Input: NativeScriptKeys.Create(code))).ToArray());
        foreach (var index in changed)
            foreach (var action in Actions(index))
            {
                if (InputMap.HasAction(action)) { Input.ActionRelease(action); InputMap.EraseAction(action); }
                InputMap.AddAction(action);
                foreach (var input in events[index])
                {
                    InputMap.ActionAddEvent(action, input.Input);
                    if (physicalKeyPressed(input.Code)) Input.ActionPress(action);
                }
            }
        _bound = bindings;
        Remapped?.Invoke(changed);
    }

    private IEnumerable<string> Actions(int control)
    {
        yield return Action(control);
        var alias = control switch
        {
            0 => configuration.MoveForward.Action,
            1 => configuration.MoveBackward.Action,
            2 => configuration.MoveLeft.Action,
            3 => configuration.MoveRight.Action,
            4 => configuration.Fire.Action,
            5 => configuration.Activate.Action,
            7 => configuration.Reload.Action,
            12 => configuration.Jump.Action,
            14 => configuration.PipBoy.Action,
            25 => configuration.Save.Action,
            27 => configuration.Grab.Action,
            _ => null,
        };
        if (alias is not null) yield return alias;
    }
}
