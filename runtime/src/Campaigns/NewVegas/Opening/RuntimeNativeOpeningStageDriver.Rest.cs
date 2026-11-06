using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Presentation.Ui;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    private CanvasLayer? _restLayer;
    private NativeOwnedSleepWaitMenu? _restMenu;
    private Action? _releaseRestInput;
    private IDisposable? _restBackground;
    private bool _restWasPaused, _restOwnsPause, _wakeRequested;
    private FalloutRestKind _restKind;
    internal object? RestState => _restMenu?.State;

    // A player menu, character-creation entry, movie, furniture or terminal
    // owns input. Menus that dialogue result scripts open (repair, casino)
    // admit an active conversation; resting does not.
    private bool PlayerMenuOwnsInput => _moviePlaying || _levelUpMenu is not null || _restMenu is not null || _repairServicesMenu is not null ||
        _player.FurnitureActive || _recipeMenu is not null || _barterMenu is not null || _caravanMenu is not null || _casinoMenu is not null ||
        _nameEntry is not null || _raceSexEntry is not null || _vigorEntry is not null || _specialBookEntry is not null ||
        _tagSkillEntry is not null || _traitEntry is not null || TerminalSaveBlocker is not null;

    private bool PlayerInteractionBusy => PlayerMenuOwnsInput || _conversation?.Active == true || _speech?.Active == true;

    // Beds, the Wait control and ShowSleepWaitMenu share one admission path.
    // A refusal is published as the executable's HUD notice, never thrown.
    internal void RequestPlayerRest(FalloutRestKind kind, FalloutFormKey? bed)
    {
        if (PlayerInteractionBusy || _vitals.State.ExactHitPoints <= 0)
        {
            // Not a source refusal notice, but never a silent drop either.
            GD.Print($"OPENNV_REST_IGNORED kind={kind} bed={bed?.ToString() ?? "none"} reason={(PlayerInteractionBusy ? "interaction-busy" : "dead")}");
            return;
        }
        var world = _scripts.References ?? throw new InvalidOperationException("Rest has no reference-world owner.");
        var player = _pluginStack.RuntimeFormKey(0x14);
        var factions = world.ActorFactions(player);
        var circumstances = new FalloutRestCircumstances(world.PlayerInCombat(), !_player.IsOnFloor(),
            bed is { } placed && FalloutPlayerRest.OwnedByOther(_pluginStack, world.Ownership(placed),
                faction => factions.TryGetValue(faction, out var rank) && rank >= 0 ? rank : null,
                global => (_globals ?? throw new NotSupportedException("Bed ownership global has no shared state owner.")).Get(global)));
        if (FalloutPlayerRest.Refusal(kind, circumstances) is { } refusal)
        {
            // HUD notices resolve their source record; the engine player is
            // the player base record, as the quicksave notice uses.
            var notice = new FalloutHudEvent(FalloutHudEventKind.ExecutableNotice, _raceSexContract.Player, 0, Text: refusal);
            _inventory.Notifications.RequirePublish([notice]);
            _inventory.Notifications.Publish([notice]);
            GD.Print($"OPENNV_REST_REFUSED kind={kind} bed={bed?.ToString() ?? "none"} reason={refusal}");
            return;
        }
        OpenRestMenu(kind, bed);
    }

    // The Wait control is ordinary input: a fault stays visible as driver
    // divergence instead of escaping into the input dispatcher.
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
        if (_restMenu is not null) throw new InvalidOperationException("A Sleep/Wait menu is already active.");
        var clock = _gameTime ?? throw new NotSupportedException("Sleep/Wait has no shared game clock.");
        var layer = new CanvasLayer { Layer = 94, ProcessMode = ProcessModeEnum.Always };
        var menu = new NativeOwnedSleepWaitMenu(_pluginStack, kind, RestClockText, () =>
        {
            clock.AdvanceFastTravel(1);
            return !_wakeRequested;
        }, hours => CompleteRest(kind, hours));
        _restLayer = layer; _restMenu = menu; _restKind = kind; _wakeRequested = false;
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
        GD.Print($"OPENNV_REST_MENU_OPEN kind={kind} bed={bed?.ToString() ?? "none"} hour={clock.Hour:R} source=sleep_wait_menu.xml parity=unverified");
    }

    private string RestClockText()
    {
        var hour = (_gameTime ?? throw new NotSupportedException("Sleep/Wait has no shared game clock.")).ScheduleTime().Hour;
        var whole = (int)hour;
        var minutes = Math.Clamp((int)((hour - whole) * 60), 0, 59);
        var twelve = whole % 12 == 0 ? 12 : whole % 12;
        var suffix = FalloutGameSettingStrings.Read(_pluginStack, whole < 12 ? "sAMTime" : "sPMTime");
        return $"{twelve}:{minutes:00} {suffix}";
    }

    private void CompleteRest(FalloutRestKind kind, int hours)
    {
        try { ApplyRestRecovery(kind, hours); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            RetainDriverFailure(error);
            GD.PushError($"OPENNV_REST_FAIL {error.Message}");
        }
        finally { CloseRestMenu(); }
    }

    private void ApplyRestRecovery(FalloutRestKind kind, int hours)
    {
        // Hardcore needs have no accrual owner yet, so there is no sleep
        // deprivation to restore here; health follows the shared rest rule.
        _vitals.Publish(FalloutPlayerRest.Recover(_vitals.State, kind, hours, _scripts.Session.Hardcore));
        GD.Print($"OPENNV_REST_COMPLETE kind={kind} hours={hours} woken={_wakeRequested} " +
            $"hour={_gameTime?.Hour:R} health={_vitals.State.HitPoints}/{_vitals.State.MaximumHitPoints}");
        _wakeRequested = false;
    }

    // SetPCSleepHours puts the player to sleep for the given hours without the
    // chooser; scripts that call it own their own fade.
    private void SleepPlayerHours(int hours)
    {
        var clock = _gameTime ?? throw new NotSupportedException("SetPCSleepHours has no shared game clock.");
        clock.ValidateFastTravel(hours);
        clock.AdvanceFastTravel(hours);
        ApplyRestRecovery(FalloutRestKind.Sleep, hours);
    }

    private bool TryApplyRestCommand(FalloutFormKey target, string[] parts, string operation, IReadOnlyList<string> arguments)
    {
        switch (operation)
        {
            case "showsleepwaitmenu" when parts.Length == 1:
                if (arguments.Count > 2 || arguments.Any(argument => argument is not ("0" or "1")))
                    throw new InvalidDataException("ShowSleepWaitMenu takes an optional sleep flag and an optional zero/one flag.");
                RequestPlayerRest(arguments.Count > 0 && arguments[0] == "1" ? FalloutRestKind.Sleep : FalloutRestKind.Wait, null);
                return true;
            case "setpcsleephours" when parts.Length == 1:
                if (arguments.Count != 1 || !double.TryParse(arguments[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var hours))
                    throw new InvalidDataException("SetPCSleepHours requires a numeric hour count.");
                SleepPlayerHours(FalloutPlayerRest.RequireHours(hours));
                return true;
            case "wakeuppc":
                if (arguments.Count != 0) throw new InvalidDataException("WakeUpPC takes no arguments.");
                if (parts.Length == 2 && _pluginStack.RuntimeFormId(target) != 0x14)
                    throw new InvalidDataException("WakeUpPC caller is not the player.");
                if (_restMenu is { Resting: true } && _restKind == FalloutRestKind.Sleep) _wakeRequested = true;
                return true;
            default:
                return false;
        }
    }

    private void CloseRestMenu()
    {
        if (_restMenu is null) return;
        var layer = _restLayer;
        _restLayer = null; _restMenu = null;
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
