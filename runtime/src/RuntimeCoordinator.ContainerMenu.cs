using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Presentation.Ui;

namespace OpenNV.Runtime;

public partial class RuntimeCoordinator
{
    // One container menu owner serves placed containers, corpses and the
    // OpenTeammateContainer command. Transfers use the shared acquisition owner.
    private void OpenNativeContainerMenu(FalloutFormKey reference, string title)
    {
        if (_nativeContainerLayer is not null) return;
        var world = _nativeReferences!;
        var inventory = world.Inventory(reference, _nativeOpeningStageDriver!.PlayerLevel, _nativeGlobals).Contents;
        var wasPaused = GetTree().Paused;
        var menu = new NativeOwnedContainerMenu(_nativePluginStack!, _nativeInventory, inventory,
            title, () =>
            {
                _nativeContainerLayer!.QueueFree(); _nativeContainerLayer = null;
                _nativeContainerReference = null;
                GetTree().Paused = wasPaused; _nativePlayer!.SetModalInput(false);
                Input.MouseMode = Input.MouseModeEnum.Captured; SaveNativeInteraction();
            }, () => { }, () => _nativePlayer!.InventoryBurden, transferWithEvents: (side, transfers) =>
            {
                var playerReference = _nativePluginStack!.RuntimeFormKey(0x14);
                _nativeOpeningStageDriver!.InventoryAcquisition.Transfer(
                    side == 1 ? inventory : _nativeInventory, side == 1 ? _nativeInventory : inventory,
                    side == 1 ? reference : playerReference, side == 1 ? playerReference : reference, transfers);
            });
        _nativePlayer!.SetModalInput(true); Input.MouseMode = Input.MouseModeEnum.Visible;
        _nativeContainerLayer = new CanvasLayer { Name = "NativeContainerLayer", Layer = 100, ProcessMode = ProcessModeEnum.Always };
        AddChild(_nativeContainerLayer);
        _nativeContainerLayer.AddChild(menu);
        _nativeContainerReference = reference;
        if (menu.Error is { } failure)
        {
            _nativeContainerLayer.QueueFree(); _nativeContainerLayer = null;
            _nativeContainerReference = null;
            GetTree().Paused = wasPaused; _nativePlayer.SetModalInput(false);
            Input.MouseMode = Input.MouseModeEnum.Captured;
            throw new NotSupportedException(failure);
        }
        GetTree().Paused = _nativeXr is null;
        GD.Print($"OPENNV_NATIVE_CONTAINER_OPEN reference={reference} items={inventory.Items.Count} source=winning-CNTO-LVLI");
    }

    // A script-opened teammate inventory waits for the current script/dialogue
    // frame to finish so the menu never opens inside a running result fragment.
    private void OpenNativeTeammateContainer(FalloutFormKey actor)
    {
        if (_nativeReferences is not { } world) throw new InvalidOperationException("Teammate container has no reference world.");
        var title = world.ActorDisplayName(actor);
        Callable.From(() => OpenNativeContainerMenu(actor, title)).CallDeferred();
        GD.Print($"OPENNV_NATIVE_TEAMMATE_CONTAINER_REQUEST actor={actor} source=OpenTeammateContainer");
    }
}
