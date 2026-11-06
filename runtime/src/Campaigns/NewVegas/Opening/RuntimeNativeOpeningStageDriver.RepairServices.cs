using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Presentation.Ui;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    private CanvasLayer? _repairServicesLayer;
    private NativeOwnedRepairServicesMenu? _repairServicesMenu;
    private Action? _releaseRepairServicesInput;
    private bool _repairServicesWasPaused, _repairServicesOwnsPause;
    internal object? RepairServicesState => _repairServicesMenu?.State;

    private bool TryApplyRepairServicesCommand(FalloutFormKey target, string[] parts, string operation, IReadOnlyList<string> arguments)
    {
        if (operation != "showrepairmenu" || parts.Length > 2) return false;
        if (arguments.Count != 0) throw new InvalidDataException("ShowRepairMenu takes no arguments.");
        if (_pluginStack.RuntimeFormId(target) == 0x14 || _pluginStack.GetEffective(target).Signature is not ("ACHR" or "ACRE"))
            throw new InvalidDataException("ShowRepairMenu caller is not a placed vendor actor.");
        OpenRepairServicesMenu(target);
        return true;
    }

    private void OpenRepairServicesMenu(FalloutFormKey vendor)
    {
        if (_repairServicesMenu is not null) throw new InvalidOperationException("A repair services menu is already active.");
        if (PlayerMenuOwnsInput)
            throw new InvalidOperationException("Repair services cannot open while another player menu owns input.");
        var world = _scripts.References ?? throw new InvalidOperationException("Repair services have no reference-world owner.");
        var skill = world.ActorValue(vendor, "Repair");
        var caps = FalloutDialogueTopic.Find(_pluginStack, "MISC", "Caps001").FormKey;
        var layer = new CanvasLayer { Layer = 94, ProcessMode = ProcessModeEnum.Always };
        var menu = new NativeOwnedRepairServicesMenu(_pluginStack, skill,
            () => FalloutRepairServices.Quotes(_pluginStack, _inventory, skill), () => _inventory.Item(caps)?.Count ?? 0,
            instance =>
            {
                var quote = _inventory.RepairByVendor(_pluginStack, instance.Form, instance.Variant, skill, caps, _inventory.Revision);
                GD.Print($"OPENNV_REPAIR_SERVICE vendor={vendor} item={instance.Form} from={quote.Condition:R} to={quote.Target:R} cost={quote.Cost}");
            }, CloseRepairServicesMenu);
        _repairServicesLayer = layer; _repairServicesMenu = menu;
        try
        {
            _releaseRepairServicesInput = AcquireInteractionInput();
            _repairServicesWasPaused = GetTree().Paused; GetTree().Paused = true; _repairServicesOwnsPause = true;
            layer.TreeExiting += CloseRepairServicesMenu;
            menu.TreeExiting += CloseRepairServicesMenu;
            AddChild(layer);
            layer.AddChild(menu);
        }
        catch
        {
            CloseRepairServicesMenu();
            if (menu.GetParent() is null) menu.Free();
            if (!layer.IsInsideTree()) layer.Free();
            throw;
        }
        GD.Print($"OPENNV_REPAIR_SERVICES_OPEN vendor={vendor} skill={skill:R} parity=unverified");
    }

    private void CloseRepairServicesMenu()
    {
        if (_repairServicesMenu is null) return;
        var layer = _repairServicesLayer;
        _repairServicesLayer = null; _repairServicesMenu = null;
        var release = _releaseRepairServicesInput; _releaseRepairServicesInput = null;
        try { release?.Invoke(); }
        finally
        {
            if (_repairServicesOwnsPause && IsInsideTree()) GetTree().Paused = _repairServicesWasPaused;
            _repairServicesOwnsPause = false;
            if (layer is not null && !layer.IsQueuedForDeletion()) layer.QueueFree();
        }
    }
}
