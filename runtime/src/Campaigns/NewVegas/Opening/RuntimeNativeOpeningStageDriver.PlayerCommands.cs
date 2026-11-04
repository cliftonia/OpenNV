using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    private static readonly HashSet<string> DeferredScriptCommands = new(StringComparer.Ordinal);

    // Opens a living actor's inventory as a transfer container. The scene host
    // owns the container menu, input capture and pause policy.
    internal Action<FalloutFormKey>? OpenActorContainer { get; set; }

    private bool TryApplyPlayerScriptCommand(FalloutFormKey source, FalloutFormKey target, string[] parts, string operation,
        IReadOnlyList<string> arguments)
    {
        switch (operation)
        {
            case "showwarning" when parts.Length == 1:
                if (arguments.Count == 0) throw new InvalidDataException("ShowWarning requires a message string.");
                // Retail release builds route ShowWarning to the warning log,
                // not to player HUD text; keep it visible as a runtime warning.
                GD.PushWarning($"OPENNV_SCRIPT_SHOWWARNING source={source} text={Unquote(arguments[0])}");
                return true;
            case "forceterminalback" when parts.Length == 1:
                if (arguments.Count != 0) throw new InvalidDataException("ForceTerminalBack takes no arguments.");
                (_terminalMenus.Values.SingleOrDefault(menu => menu.Active) ??
                    throw new InvalidOperationException("ForceTerminalBack has no active terminal menu.")).ForceBack();
                return true;
            case "openteammatecontainer":
                if (arguments.Count > 1 || arguments.Count == 1 && arguments[0] is not ("0" or "1"))
                    throw new InvalidDataException("OpenTeammateContainer takes an optional zero/one flag.");
                if (_pluginStack.RuntimeFormId(target) == 0x14 || _pluginStack.GetEffective(target).Signature is not ("ACHR" or "ACRE"))
                    throw new InvalidDataException("OpenTeammateContainer caller is not a placed actor.");
                (OpenActorContainer ?? throw new NotSupportedException("OpenTeammateContainer has no container menu owner."))(target);
                return true;
            case "systemsave" when parts.Length == 1:
                if (arguments.Count != 0) throw new InvalidDataException("SystemSave takes no arguments.");
                RequestWorldSave();
                return true;
            case "modpcmiscstat" when parts.Length == 1:
                if (arguments.Count != 2 || !int.TryParse(arguments[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var amount))
                    throw new InvalidDataException("ModPCMiscStat requires a statistic name and an integer amount.");
                var content = RuntimeLiveContentSource.Current ??
                    throw new InvalidOperationException("Statistic declarations have no owned source.");
                _scripts.MiscStatistics.Modify(FalloutMiscStatisticNames.Index(content, Unquote(arguments[0])), amount);
                return true;
            default:
                return TryDeferPlayerScriptCommand(operation, arguments);
        }
    }

    // Presentation owners for these commands do not exist yet. Arity is still
    // validated; the reached command is reported once per name and the script
    // continues instead of stalling every later statement in its block.
    private static bool TryDeferPlayerScriptCommand(string operation, IReadOnlyList<string> arguments)
    {
        (int Minimum, int Maximum, string Reason)? deferred = operation switch
        {
            "showrepairmenu" => (0, 0, "repair-menu-presentation-unbound"),
            "showbarbermenu" => (0, 0, "barber-menu-presentation-unbound"),
            "showplasticsurgeonmenu" => (0, 0, "plastic-surgeon-menu-presentation-unbound"),
            "showroulettemenuparams" => (1, 4, "roulette-menu-presentation-unbound"),
            "showblackjackmenuparams" => (1, 4, "blackjack-menu-presentation-unbound"),
            "showslotmachinemenuparams" => (1, 4, "slot-machine-menu-presentation-unbound"),
            "showtutorialmenu" => (1, 1, "tutorial-menu-presentation-unbound"),
            "showsleepwaitmenu" => (0, 2, "sleep-wait-menu-presentation-unbound"),
            "setpcsleephours" => (1, 1, "player-sleep-owner-unbound"),
            "wakeuppc" => (0, 0, "player-sleep-owner-unbound"),
            "startmistersandman" => (0, 1, "sandman-kill-animation-owner-unbound"),
            "setpccanusepowerarmor" => (1, 1, "power-armor-training-owner-unbound"),
            "addtagskills" => (1, 1, "level-up-tag-selection-owner-unbound"),
            "addspecialpoints" => (1, 1, "level-up-special-selection-owner-unbound"),
            "setspecialpoints" => (1, 1, "special-allocation-menu-owner-unbound"),
            "resetxp" => (0, 0, "experience-reset-owner-unbound"),
            "enableloadingmenu" => (1, 1, "loading-menu-presentation-unbound"),
            "exitgame" => (0, 0, "session-exit-is-player-owned"),
            _ => null,
        };
        if (deferred is not { } rule) return false;
        if (arguments.Count < rule.Minimum || arguments.Count > rule.Maximum)
            throw new InvalidDataException($"{operation} has an invalid argument count {arguments.Count}.");
        bool first;
        lock (DeferredScriptCommands) first = DeferredScriptCommands.Add(operation);
        if (first) GD.PushWarning($"OPENNV_SCRIPT_COMMAND_DEFERRED name={operation} reason={rule.Reason}");
        return true;
    }

    private static string Unquote(string token) => token.Length >= 2 && token[0] == '"' && token[^1] == '"'
        ? token[1..^1] : token;
}
