using Godot;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{

    // Opens a living actor's inventory as a transfer container. The scene host
    // owns the container menu, input capture and pause policy.
    internal Action<FalloutFormKey>? OpenActorContainer { get; set; }

    // Ends the play session (the ending slideshow's final command). The scene
    // host owns the credits roll and the return to the main menu.
    internal Action? ExitGame { get; set; }

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
            case "exitgame" when parts.Length == 1:
                if (arguments.Count != 0) throw new InvalidDataException("ExitGame takes no arguments.");
                GD.Print($"OPENNV_SCRIPT_EXITGAME source={source}");
                (ExitGame ?? throw new NotSupportedException("ExitGame has no session owner."))();
                return true;
            // Victim.StartMisterSandman Player: the Mister Sandman perk's sleeping
            // victim dies with the player as killer. The paired kill idle has no
            // player third-person presentation owner and is reported once.
            case "startmistersandman":
                if (arguments.Count > 1 || arguments.Count == 1 && Unquote(arguments[0]).ToLowerInvariant() is not ("player" or "playerref"))
                    throw new InvalidDataException("StartMisterSandman takes an optional player killer.");
                if (_pluginStack.RuntimeFormId(target) == 0x14 || _pluginStack.GetEffective(target).Signature is not ("ACHR" or "ACRE"))
                    throw new InvalidDataException("StartMisterSandman caller is not a placed actor.");
                var killed = (_scripts.References ?? throw new InvalidOperationException("StartMisterSandman has no world owner."))
                    .KillActor(target, _pluginStack.RuntimeFormKey(0x14), SourcePlayerLevel, _globals);
                World.Cells.FalloutScriptCommandDeferral.Warn(operation, "sandman-kill-idle-presentation-unbound");
                GD.Print($"OPENNV_SCRIPT_SANDMAN_KILL victim={target} killed={killed}");
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
                return TryApplyRestCommand(target, parts, operation, arguments) ||
                    TryApplyProgressionCommand(parts, operation, arguments) ||
                    TryApplyRepairServicesCommand(target, parts, operation, arguments) ||
                    TryDeferPlayerScriptCommand(operation, arguments);
        }
    }

    // Presentation owners for these commands do not exist yet. Arity is still
    // validated; the reached command is reported once per name and the script
    // continues instead of stalling every later statement in its block.
    private static bool TryDeferPlayerScriptCommand(string operation, IReadOnlyList<string> arguments)
    {
        (int Minimum, int Maximum, string Reason)? deferred = operation switch
        {
            "showtutorialmenu" => (1, 1, "tutorial-menu-presentation-unbound"),
            "setpccanusepowerarmor" => (1, 1, "power-armor-training-owner-unbound"),
            _ => null,
        };
        if (deferred is not { } rule) return false;
        if (arguments.Count < rule.Minimum || arguments.Count > rule.Maximum)
            throw new InvalidDataException($"{operation} has an invalid argument count {arguments.Count}.");
        World.Cells.FalloutScriptCommandDeferral.Warn(operation, rule.Reason);
        return true;
    }

    private static string Unquote(string token) => token.Length >= 2 && token[0] == '"' && token[^1] == '"'
        ? token[1..^1] : token;
}
