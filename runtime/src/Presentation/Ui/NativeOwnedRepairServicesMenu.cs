using System.Globalization;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Presentation.Ui;

/// <summary>RepairServicesMenu: damaged player equipment, the vendor's Repair-capped result and its caps price.</summary>
internal sealed partial class NativeOwnedRepairServicesMenu : Control
{
    private readonly FalloutPluginStack _records;
    private readonly Func<IReadOnlyList<FalloutRepairQuote>> _quotes;
    private readonly Func<int> _caps;
    private readonly Action<FalloutInventoryInstance> _repair;
    private readonly Action _close;
    private readonly VBoxContainer _rows;
    private readonly Label _header, _detail, _message;
    private readonly Button _repairButton, _repairAllButton;
    private FalloutInventoryInstance? _selected;
    private bool _closed;
    internal object State => new { selected = _selected?.Form.ToString(), message = _message.Text, quotes = _quotes().Count };

    internal NativeOwnedRepairServicesMenu(FalloutPluginStack records, float vendorSkill,
        Func<IReadOnlyList<FalloutRepairQuote>> quotes, Func<int> caps, Action<FalloutInventoryInstance> repair, Action close)
    {
        Name = "OwnedRepairServicesMenu"; ProcessMode = ProcessModeEnum.Always; MouseFilter = MouseFilterEnum.Stop;
        _records = records; _quotes = quotes; _caps = caps; _repair = repair; _close = close;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Name = "Backdrop", Color = new(0.015f, 0.02f, 0.03f, 0.88f), MouseFilter = MouseFilterEnum.Stop, AnchorRight = 1, AnchorBottom = 1 });
        var center = new CenterContainer { Name = "Center" };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(center);
        var panel = new PanelContainer { Name = "RepairServicesPanel", CustomMinimumSize = new(900, 620) };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new(0.035f, 0.045f, 0.055f, 0.98f), BorderColor = new(0.64f, 0.74f, 0.54f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            ContentMarginLeft = 22, ContentMarginTop = 18, ContentMarginRight = 22, ContentMarginBottom = 18,
        });
        center.AddChild(panel);
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 10); panel.AddChild(column);
        var title = new Label { Name = "RepairServicesTitle", Text = Setting("sRepairServicesTitle"), HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 26); column.AddChild(title);
        _header = new Label { Name = "RepairServicesHeader", HorizontalAlignment = HorizontalAlignment.Center };
        column.AddChild(_header);
        var scroll = new ScrollContainer { Name = "RepairServicesScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(scroll);
        _rows = new VBoxContainer { Name = "RepairServicesRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 3); scroll.AddChild(_rows);
        _detail = new Label { Name = "RepairServicesDetail", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_detail);
        _message = new Label { Name = "RepairServicesMessage", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_message);
        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddThemeConstantOverride("separation", 10); column.AddChild(actions);
        _repairButton = new Button { Name = "RSM_RepairButton", Text = Setting("sRepairItem"), CustomMinimumSize = new(140, 44), FocusMode = FocusModeEnum.All };
        _repairButton.Pressed += RepairSelected; actions.AddChild(_repairButton);
        _repairAllButton = new Button { Name = "RSM_RepairAllButton", CustomMinimumSize = new(200, 44), FocusMode = FocusModeEnum.All };
        _repairAllButton.Pressed += RepairAll; actions.AddChild(_repairAllButton);
        var done = new Button { Name = "RSM_DoneButton", Text = Setting("sDone"), CustomMinimumSize = new(130, 44), FocusMode = FocusModeEnum.All };
        done.Pressed += Close; actions.AddChild(done);
        _header.Text = $"{Setting("sRepairSkill")}: {Math.Round(vendorSkill).ToString(CultureInfo.InvariantCulture)}";
        SetMeta("opennv_menu_id", (int)FalloutRepairServices.MenuId);
        SetMeta("opennv_ui_source", "repair-services strings; vendor Repair skill; source item-condition value settings; fItemRepairCostMult");
        SetMeta("opennv_ui_unverified", "repair_services_menu.xml-layout,barter-price-modifiers,matched-pixels");
        Refresh();
    }

    private string Setting(string name) => FalloutGameSettingStrings.Read(_records, name);

    private static string Percent(float condition) => Math.Round(condition * 100).ToString(CultureInfo.InvariantCulture) + "%";

    private string NameOf(FalloutFormKey form) => FalloutRecordDisplayName.Read(_records.GetEffective(form));

    private void Refresh()
    {
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        var quotes = _quotes();
        var caps = _caps();
        _header.Text = _header.Text.Split("   ")[0] + $"   {Setting("sInventoryCaps")}: {caps.ToString(CultureInfo.InvariantCulture)}";
        if (quotes.Count == 0) _rows.AddChild(new Label { Name = "RSM_EmptyRepairListText", Text = Setting("sNoItemsToRepair") });
        foreach (var quote in quotes)
        {
            var instance = quote.Instance;
            var text = $"{NameOf(instance.Form)}   {Percent(quote.Condition)}" +
                (quote.Target is { } target ? $" → {Percent(target)}   {Format(Setting("sRepairCost"), quote.Cost)}" : "");
            var button = new Button { Text = text, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = FocusModeEnum.All,
                ButtonPressed = Same(instance, _selected) };
            button.Pressed += () => { _selected = instance; _message.Text = ""; Refresh(); };
            _rows.AddChild(button);
        }
        var selected = quotes.FirstOrDefault(quote => Same(quote.Instance, _selected));
        _selected = selected?.Instance;
        _detail.Text = selected is null ? "" : selected.Target is null
            ? string.Format(CultureInfo.InvariantCulture, CFormat(Setting("sRepairSkillTooLow")),
                (int)Math.Floor(selected.Condition * 100) + 1, "%")
            : $"{NameOf(selected.Instance.Form)}: {Percent(selected.Condition)} → {Percent(selected.Target.Value)}   " +
              Format(Setting("sRepairCost"), selected.Cost);
        _repairButton.Disabled = selected?.Target is null || selected.Cost > caps;
        var repairable = quotes.Where(quote => quote.Target is not null).ToArray();
        var total = repairable.Sum(quote => (long)quote.Cost);
        _repairAllButton.Text = Format(Setting("sRepairAllItems"), (int)Math.Min(total, int.MaxValue));
        _repairAllButton.Disabled = repairable.Length == 0 || total > caps;
    }

    private static bool Same(FalloutInventoryInstance value, FalloutInventoryInstance? other) =>
        other is not null && value.Form == other.Form && value.Variant == other.Variant;

    // Retail strings carry C %d / %s tokens.
    private static string CFormat(string source) => source.Replace("%d", "{0}").Replace("%s", "{1}");
    private static string Format(string source, int value) =>
        string.Format(CultureInfo.InvariantCulture, CFormat(source), value, "");

    private void RepairSelected()
    {
        if (_closed || _selected is not { } selected) return;
        Try(() => _repair(selected));
    }

    private void RepairAll()
    {
        if (_closed) return;
        Try(() =>
        {
            // Each repair re-quotes from live inventory; variant indices can
            // shift after a stack is split, so take the next quote each time.
            while (_quotes().FirstOrDefault(quote => quote.Target is not null) is { } next) _repair(next.Instance);
        });
    }

    private void Try(Action action)
    {
        try { action(); _message.Text = ""; }
        catch (InvalidOperationException error) { _message.Text = error.Message; }
        Refresh();
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true; _close();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (_closed || inputEvent is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.PhysicalKeycode is Key.Escape or Key.Tab or Key.E) { Close(); GetViewport().SetInputAsHandled(); }
        else if (key.PhysicalKeycode == Key.A) { RepairAll(); GetViewport().SetInputAsHandled(); }
    }
}
