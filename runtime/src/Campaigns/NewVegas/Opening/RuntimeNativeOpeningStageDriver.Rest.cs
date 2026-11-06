using System.Globalization;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Presentation.Ui;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    private CanvasLayer? _restLayer;
    private NativeOwnedSleepWaitMenu? _restMenu;
    private RuntimeNativeReferenceEvents? _restReferenceEvents;
    private Action? _releaseRestInput;
    private IDisposable? _restBackground;
    private bool _restWasPaused, _restOwnsPause, _restInBed;
    private FalloutRestKind _restKind;
    private int _restHoursRun;
    private string _restUnbound = "", _restHealingUnbound = "";
    internal object? RestState => _scripts is null ? null : new
    {
        menu = _restMenu?.State, sleeping = _scripts.Session.PlayerSleeping, hours = _scripts.Session.PlayerSleepHours,
        controlsEnabled = PlayerControls == FalloutPlayerControlState.AllEnabled, unbound = _restUnbound,
        healingUnbound = _restHealingUnbound,
    };

    private bool PlayerInteractionBusy => _moviePlaying || _levelUpMenu is not null || _restMenu is not null || _repairServicesMenu is not null ||
        _player.FurnitureActive || _recipeMenu is not null || _barterMenu is not null || _caravanMenu is not null || _casinoMenu is not null ||
        _nameEntry is not null || _raceSexEntry is not null || _vigorEntry is not null || _specialBookEntry is not null ||
        _tagSkillEntry is not null || _traitEntry is not null || _conversation?.Active == true || _speech?.Active == true ||
        TerminalSaveBlocker is not null;

    // The Wait control does nothing while any player control is disabled.
    private void RunWaitControl()
    {
        if (PlayerControls != FalloutPlayerControlState.AllEnabled) return;
        RunPlayerRestRequest(FalloutRestKind.Wait, null);
    }

    // Beds and the Wait control use their own original admission chain. A
    // refusal is published as the executable's HUD notice, never thrown.
    internal void RequestPlayerRest(FalloutRestKind kind, FalloutFormKey? bed)
    {
        if (PlayerInteractionBusy || _vitals.State.ExactHitPoints <= 0) return;
        var circumstances = RestCircumstances(bed);
        var refusal = kind == FalloutRestKind.Sleep ? FalloutPlayerRest.SleepRefusal(circumstances) : FalloutPlayerRest.WaitRefusal(circumstances);
        if (refusal is not null) { RefuseRest(kind, bed, refusal); return; }
        OpenRestMenu(kind, bed);
    }

    private void RefuseRest(FalloutRestKind kind, FalloutFormKey? bed, string refusal)
    {
        // HUD notices resolve their source record; the engine player is the
        // player base record, as the quicksave notice uses.
        _inventory.Notifications.Publish([new(FalloutHudEventKind.ExecutableNotice, _raceSexContract.Player, 0, Text: refusal)]);
        GD.Print($"OPENNV_REST_REFUSED kind={kind} bed={bed?.ToString() ?? "none"} reason={refusal} unbound={_restUnbound}");
    }

    // Original admission inputs that read player states OpenNV does not model
    // yet admit the request; each request names them in its telemetry.
    private FalloutRestCircumstances RestCircumstances(FalloutFormKey? bed)
    {
        var world = _scripts.References ?? throw new InvalidOperationException("Rest has no reference-world owner.");
        var player = _pluginStack.RuntimeFormKey(0x14);
        // An unowned or public cell is never trespassed in; elsewhere the
        // trespass state has no owner yet.
        var unowned = world.HasUnownedCell(player, SharedConditions.PlayerPlacement?.Invoke(), _player.UnitsToMeters);
        _restUnbound = (unowned ? "" : "trespass,") + "alarm,underwater,radiation,conversation-target-distance";
        var limit = FalloutGameSettingFloats.Read(_pluginStack,
            IsInInterior(player) ? "fHostileActorInteriorDistance" : "fHostileActorExteriorDistance");
        var factions = world.ActorFactions(player);
        return new(HostileActorsNear: world.HostileActorNear(limit, actor => ReferenceDistance(actor, player)),
            Airborne: !_player.IsOnFloor(), CantWaitHere: FalloutPlayerRest.CantWait(_pluginStack, _activeCell),
            WaitDisabled: !FastTravel.State.CanWait, TakingHealthDamage: _ingestibles.TakingHealthDamage,
            BedOwnedByOther: bed is { } placed && FalloutPlayerRest.OwnedByOther(_pluginStack, world.Ownership(placed),
                faction => factions.TryGetValue(faction, out var rank) && rank >= 0 ? rank : null,
                global => (_globals ?? throw new NotSupportedException("Bed ownership global has no shared state owner.")).Get(global)));
    }

    // Ordinary input: a fault stays visible as driver divergence instead of
    // escaping into the input dispatcher.
    private void RunPlayerRestRequest(FalloutRestKind kind, FalloutFormKey? bed)
    {
        try { RequestPlayerRest(kind, bed); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            RetainDriverFailure(error);
            GD.PushError($"OPENNV_REST_FAIL {error.Message}");
        }
    }

    private void OpenRestMenu(FalloutRestKind kind, FalloutFormKey? bed)
    {
        // An already open Sleep/Wait menu is replaced.
        if (_restMenu is not null) CloseRestMenu();
        var clock = _gameTime ?? throw new NotSupportedException("Sleep/Wait has no shared game clock.");
        var layer = new CanvasLayer { Layer = 94, ProcessMode = ProcessModeEnum.Always };
        var menu = new NativeOwnedSleepWaitMenu(_pluginStack, kind, RestClockText, hours =>
        {
            _scripts.Session.PlayerSleepHours = hours;
            _scripts.Session.PlayerSleeping = kind == FalloutRestKind.Sleep;
        }, MenuRestHourStep, () => _scripts.Session.PlayerSleepHours, CloseRestMenu);
        _restLayer = layer; _restMenu = menu; _restKind = kind; _restInBed = bed is not null; _restHoursRun = 0;
        _restReferenceEvents = GetTree().Root.FindChildren("*", "", true, false).OfType<RuntimeNativeReferenceEvents>()
            .SingleOrDefault(owner => owner.IsProcessing());
        try
        {
            _releaseRestInput = AcquireInteractionInput();
            _restWasPaused = GetTree().Paused; GetTree().Paused = true; _restOwnsPause = true;
            _restBackground = _imageSpacePresenter().BeginMenuBackground(_pluginStack, FalloutMenuBackgroundKind.Popup);
            layer.TreeExiting += CloseRestMenu;
            menu.TreeExiting += CloseRestMenu;
            AddChild(layer);
            layer.AddChild(menu);
            if (menu.Error is { } error) throw new InvalidOperationException("Sleep/Wait presentation failed: " + error);
        }
        catch
        {
            CloseRestMenu();
            if (menu.GetParent() is null) menu.Free();
            if (!layer.IsInsideTree()) layer.Free();
            throw;
        }
        GD.Print($"OPENNV_REST_MENU_OPEN kind={kind} bed={bed?.ToString() ?? "none"} hour={clock.Hour:R} unbound={_restUnbound} " +
            "source=sleep_wait_menu.xml parity=unverified");
    }

    // Resident object scripts run their MenuMode blocks while the menu is open
    // (the player bed script marks an actual sleep with IsPCSleeping).
    private void DispatchRestMenuMode(double delta)
    {
        if (_restReferenceEvents is { } events && GodotObject.IsInstanceValid(events)) events.DispatchMenuMode(delta);
    }

    // A failed hour stays visible as driver divergence; the menu then closes.
    private int MenuRestHourStep()
    {
        try { return RestHourStep(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            RetainDriverFailure(error);
            GD.PushError($"OPENNV_REST_FAIL {error.Message}");
            throw;
        }
    }

    private string RestClockText()
    {
        var clock = _gameTime ?? throw new NotSupportedException("Sleep/Wait has no shared game clock.");
        return FalloutPlayerRest.ClockText(_pluginStack, clock.ScheduleTime(), clock.Year);
    }

    // One simulated hour: Hardcore needs (seeing time up to the previous
    // step), the clock, the player's effects, health regeneration, Hardcore
    // sleep restoration, then the counter. A natural Sleep end outside
    // Hardcore fully heals. Returns the hours left.
    private int RestHourStep()
    {
        var clock = _gameTime ?? throw new NotSupportedException("Sleep/Wait has no shared game clock.");
        var session = _scripts.Session;
        if (session.Hardcore) AdvanceHardcoreNeeds();
        var seconds = 3600 / clock.TimeScale;
        clock.AdvanceFastTravel(1);
        _ingestibles.Advance(seconds);
        _playerActorValues.AdvanceFatigue(seconds);
        _deliveredEffects.Advance(seconds);
        var state = FalloutPlayerRest.Heal(_vitals.State,
            FalloutPlayerRest.HourHealing(_pluginStack, PlayerHealRate(), _restInBed && session.PlayerSleeping, seconds));
        if (session.Hardcore && session.PlayerSleeping) state = _needs.RestoreSleep(state);
        _vitals.Publish(state);
        _restHoursRun++;
        session.PlayerSleepHours--;
        if (session.PlayerSleepHours <= 0)
        {
            if (session.PlayerSleeping && !session.Hardcore)
            {
                _vitals.Publish(FalloutPlayerRest.EndOfSleep(_vitals.State));
                _playerActorValues.ClearDamage(22);
            }
            ReportRest(natural: true);
            session.PlayerSleepHours = 0;
            session.PlayerSleeping = false;
        }
        return session.PlayerSleepHours;
    }

    // The HealRate actor value has no player owner yet, and the Modify
    // Recovered Health perk term none at all: the hour then heals only by
    // the sleeping base rate, and the rest names both as unbound.
    private float PlayerHealRate()
    {
        try
        {
            var rate = (float)ReadPlayerActorValue("HealRate", World.Actors.FalloutActorValueRead.Current);
            _restHealingUnbound = "recovered-health-perk";
            return rate;
        }
        catch (NotSupportedException)
        {
            _restHealingUnbound = "heal-rate,recovered-health-perk";
            return 0;
        }
    }

    private void ReportRest(bool natural) =>
        GD.Print($"OPENNV_REST_COMPLETE kind={(_restMenu is null ? FalloutRestKind.Sleep : _restKind)} hours={_restHoursRun} natural={natural} " +
            $"hour={_gameTime?.Hour:R} health={_vitals.State.HitPoints}/{_vitals.State.MaximumHitPoints} healingUnbound={_restHealingUnbound}");

    private bool TryApplyRestCommand(FalloutFormKey target, string[] parts, string operation, IReadOnlyList<string> arguments)
    {
        var session = _scripts.Session;
        switch (operation)
        {
            case "showsleepwaitmenu" when parts.Length == 1:
                if (arguments.Count > 2 || arguments.Any(argument => argument is not ("0" or "1")))
                    throw new InvalidDataException("ShowSleepWaitMenu takes an optional sleep flag and an optional zero/one check flag.");
                if (_vitals.State.ExactHitPoints <= 0 || PlayerInteractionBusy && _restMenu is null)
                {
                    GD.Print($"OPENNV_REST_SHOW_MENU_DROPPED reason={(_vitals.State.ExactHitPoints <= 0 ? "dead" : "interaction-busy")}");
                    return true;
                }
                if (arguments.Count > 1 && arguments[1] == "1")
                {
                    // The checked form runs the Wait chain, then opens Sleep.
                    if (FalloutPlayerRest.WaitRefusal(RestCircumstances(null)) is { } refusal)
                        RefuseRest(FalloutRestKind.Wait, null, refusal);
                    else OpenRestMenu(FalloutRestKind.Sleep, null);
                }
                else OpenRestMenu(arguments.Count > 0 && arguments[0] == "1" ? FalloutRestKind.Sleep : FalloutRestKind.Wait, null);
                return true;
            case "setpcsleephours" when parts.Length == 1:
                if (arguments.Count != 1 || !double.TryParse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ||
                    hours != Math.Truncate(hours) || hours < 1 || hours > int.MaxValue)
                    throw new InvalidDataException("SetPCSleepHours requires a positive whole hour count.");
                // Scripts that call it own their own fade; the player's
                // update then runs one rest hour per frame.
                session.PlayerSleepHours = (int)hours;
                session.PlayerSleeping = true;
                if (_restMenu is null) { _restInBed = false; _restHoursRun = 0; }
                return true;
            case "wakeuppc":
                if (arguments.Count > 1) throw new InvalidDataException("WakeUpPC takes an optional hour count.");
                if (parts.Length == 2 && _pluginStack.RuntimeFormId(target) != 0x14)
                    throw new InvalidDataException("WakeUpPC caller is not the player.");
                if (!session.PlayerSleeping) return true;
                var remaining = 0;
                if (arguments.Count == 1 && (!int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out remaining)))
                    throw new InvalidDataException("WakeUpPC hours must be a source integer.");
                session.PlayerSleepHours = Math.Max(remaining, 0);
                if (session.PlayerSleepHours == 0)
                {
                    ReportRest(natural: false);
                    session.PlayerSleeping = false;
                    CloseRestMenu();
                }
                return true;
            default:
                return false;
        }
    }

    // Cancel, WakeUpPC, a finished counter, a replaced or removed menu: closing
    // clears the counter and the sleeping flag without the end-of-sleep heal.
    private void CloseRestMenu()
    {
        if (_restMenu is null) return;
        var session = _scripts.Session;
        if (session.PlayerSleepHours > 0) ReportRest(natural: false);
        session.PlayerSleepHours = 0;
        session.PlayerSleeping = false;
        var layer = _restLayer;
        _restLayer = null; _restMenu = null; _restReferenceEvents = null;
        var release = _releaseRestInput; _releaseRestInput = null;
        var background = _restBackground; _restBackground = null;
        try { background?.Dispose(); }
        finally
        {
            try { release?.Invoke(); }
            finally
            {
                if (_restOwnsPause && IsInsideTree()) GetTree().Paused = _restWasPaused;
                _restOwnsPause = false;
                if (layer is not null && !layer.IsQueuedForDeletion()) layer.QueueFree();
            }
        }
    }
}
