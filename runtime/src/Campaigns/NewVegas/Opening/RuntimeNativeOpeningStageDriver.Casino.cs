using System.Globalization;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Presentation.Rendering;
using OpenNV.Runtime.Presentation.Ui;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    private CanvasLayer? _casinoLayer;
    private NativeCasinoMenu? _casinoMenu;
    private Action? _releaseCasinoInput;
    internal object? CasinoState => _casinoMenu?.State;

    // Executable menu identities: each menu constructor pushes its own id
    // (slot machine 1080, blackjack 1081, roulette 1082; caravan is 1083).
    internal static uint CasinoMenuId(FalloutCasinoGame game) => game switch
    {
        FalloutCasinoGame.SlotMachine => 1080,
        FalloutCasinoGame.Blackjack => 1081,
        FalloutCasinoGame.Roulette => 1082,
        _ => throw new NotSupportedException("Unowned casino game."),
    };

    private bool TryApplyCasinoCommand(FalloutScriptBindings bindings, string operation, IReadOnlyList<string> arguments)
    {
        if (operation is not ("showblackjackmenuparams" or "showroulettemenuparams" or "showslotmachinemenuparams")) return false;
        if (arguments.Count != 4) throw new InvalidDataException($"{operation} requires casino, min bet, max bet and max winnings.");
        double Number(string token) => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException($"{operation} numeric arguments were not resolved.");
        var request = FalloutCasinoCommandRequest.Read(_pluginStack, FalloutCasinoCommandRequest.GameFor(operation),
            bindings.Form(arguments[0]), Number(arguments[1]), Number(arguments[2]), Number(arguments[3]));
        OpenCasinoMenu(request);
        return true;
    }

    private void OpenCasinoMenu(FalloutCasinoCommandRequest request)
    {
        if (_casinoMenu is not null || _caravanMenu is not null || _player.FurnitureActive || _moviePlaying || _recipeMenu is not null ||
            _barterMenu is not null || _levelUpMenu is not null || _nameEntry is not null || _raceSexEntry is not null ||
            _vigorEntry is not null || _specialBookEntry is not null || _tagSkillEntry is not null || _traitEntry is not null ||
            TerminalSaveBlocker is not null)
            throw new InvalidOperationException("A casino game cannot open while another player interaction owns input.");
        var world = _scripts.References ?? throw new InvalidOperationException("Casino has no shared world owner.");
        var source = RuntimeLiveContentSource.Current ?? throw new InvalidOperationException("Casino owned presentation source is absent.");
        var player = _pluginStack.RuntimeFormKey(0x14);
        var chip = request.Casino.Chip;
        var statistic = ResolveCasinoStatistic(source, request.Game);
        var luck = (int)Math.Round(ReadPlayerActorValue("Luck", World.Actors.FalloutActorValueRead.Current));
        var table = new FalloutCasinoTable(request, world.CasinoWinnings,
            () => checked((int)InventoryCommands.ItemCount(player, chip)),
            delta => InventoryCommands.Execute(new(delta > 0 ? FalloutInventoryCommandKind.Add : FalloutInventoryCommandKind.Remove,
                player, Item: chip, Count: Math.Abs(delta), Silent: true)),
            luck, BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(sizeof(ulong))), won =>
            {
                _scripts.MiscStatistics.Increment(statistic);
                GD.Print($"OPENNV_NATIVE_CASINO_RESULT casino={request.Casino.Form} game={request.Game} playerWon={won} " +
                    $"earnings={world.CasinoWinnings.Earnings(request.Casino.Form)} level={world.CasinoWinnings.Level(request.Casino.Form)} " +
                    "owner=shared-inventory;casino-winnings;misc-statistic payoutTable=unverified");
            });
        var textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        Texture2D? Texture(string path)
        {
            var logical = path.Replace('\\', '/');
            if (!logical.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) logical = "textures/" + logical;
            if (textures.TryGetValue(logical, out var cached)) return cached;
            if (!source.TryRead(logical, null, out var payload, out var identity)) return null;
            var texture = NativeDdsTexture.LoadCached(source, payload, identity, logicalPath: logical);
            textures.Add(logical, texture); return texture;
        }
        void Sound(string editorId)
        {
            try { _scripts.Sounds.Play(player, FalloutSoundRecordReader.Find(_pluginStack, editorId).FormKey, systemSound: true); }
            catch (Exception error) when (error is KeyNotFoundException or InvalidDataException or NotSupportedException)
            { GD.PushWarning($"OPENNV_NATIVE_CASINO_SOUND_DIVERGENCE sound={editorId} error={error.Message}"); }
        }
        var layer = new CanvasLayer { Name = "NativeCasinoLayer", Layer = 98, ProcessMode = ProcessModeEnum.Always };
        NativeCasinoMenu? menu = null;
        try
        {
            menu = new(table, CasinoMenuId(request.Game), Texture, Sound, CloseCasinoMenu, error =>
            {
                ExecutionError ??= error.Message;
                GD.PushError("OPENNV_NATIVE_CASINO_DIVERGENCE: " + error.Message);
            });
            _casinoLayer = layer; _casinoMenu = menu;
            var paused = GetTree().Paused;
            var release = AcquireInteractionInput();
            _releaseCasinoInput = () =>
            {
                try { release(); }
                finally { GetTree().Paused = paused; }
            };
            GetTree().Paused = true;
            AddChild(layer); layer.AddChild(menu);
        }
        catch
        {
            CloseCasinoMenu();
            if (menu is not null && menu.GetParent() is null && GodotObject.IsInstanceValid(menu)) menu.Free();
            if (GodotObject.IsInstanceValid(layer) && !layer.IsInsideTree()) layer.Free();
            throw;
        }
        GD.Print($"OPENNV_NATIVE_CASINO_MENU_OPEN casino={request.Casino.Form} game={request.Game} minBet={request.MinimumBet} " +
            $"maxBet={request.MaximumBet} maxWinnings={request.MaxWinnings} chips={table.Chips} luck={table.Luck} " +
            "owner=source-csno;shared-inventory;casino-winnings matched-ui=unverified");
    }

    private static int ResolveCasinoStatistic(RuntimeLiveContentSource source, FalloutCasinoGame game)
    {
        var prefix = game switch
        {
            FalloutCasinoGame.Blackjack => "Blackjack",
            FalloutCasinoGame.Roulette => "Roulette",
            FalloutCasinoGame.SlotMachine => "Slots",
            _ => throw new NotSupportedException("Unowned casino game."),
        };
        var matches = FalloutExecutableStringTable.ReadMiscStatisticNames(source.FalloutExecutablePath)
            .Where(name => name.Equals(prefix + " Games Played", StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? FalloutMiscStatisticNames.Index(source, matches[0]) :
            throw new NotSupportedException($"Owned {prefix} games statistic is absent or ambiguous.");
    }

    private void CloseCasinoMenu()
    {
        if (_casinoMenu is null) return;
        var layer = _casinoLayer;
        _casinoLayer = null; _casinoMenu = null;
        var release = _releaseCasinoInput; _releaseCasinoInput = null;
        try { release?.Invoke(); }
        finally { if (layer is not null && GodotObject.IsInstanceValid(layer) && !layer.IsQueuedForDeletion()) layer.QueueFree(); }
    }
}
