using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Presentation.Ui;

// One input surface for blackjack, roulette and slots in flat and XR. Card,
// back and reel art come from the CSNO texture declarations; the first-party
// layout is not a matched retail-pixel claim.
internal sealed partial class NativeCasinoMenu : Control
{
    private readonly FalloutCasinoTable _table;
    private readonly FalloutBlackjackGame? _blackjack;
    private readonly FalloutRouletteGame? _roulette;
    private readonly FalloutSlotMachineGame? _slots;
    private readonly Func<string, Texture2D?> _texture;
    private readonly Action<string> _sound;
    private readonly Action _close;
    private readonly Action<Exception> _failed;
    private readonly VBoxContainer _content;
    private int _bet;
    private string _rouletteMode = "straight";
    private bool _closing, _faulted;
    private string? _message;
    private NativeViewportLayout? _viewportLayout;
    internal uint MenuId { get; }
    internal FalloutCasinoGame Game => _table.Request.Game;

    internal object State => new
    {
        casino = _table.Casino.Form.ToString(),
        game = Game.ToString(),
        chips = _table.Chips,
        earnings = _table.Earnings,
        banned = _table.Banned,
        bet = _bet,
        minimumBet = _table.Request.MinimumBet,
        maximumBet = _table.Request.MaximumBet,
        maxWinnings = _table.Request.MaxWinnings,
        phase = _blackjack?.Current.ToString(),
        player = _blackjack?.Hands.Select(hand => hand.Cards.Select(card => card.ToString()).ToArray()).ToArray(),
        dealer = _blackjack?.Dealer.Select((card, index) => index == 1 && !_blackjack.DealerRevealed ? "??" : card.ToString()).ToArray(),
        rouletteBets = _roulette?.Bets.Select(bet => bet.Label + ":" + bet.Amount).ToArray(),
        pocket = _roulette?.LastPocket is { } pocket ? FalloutRouletteGame.Name(pocket) : null,
        reels = _slots?.Symbols,
        message = _message ?? GameMessage,
        unverified = "slot-payout-table;winnings-level-thresholds;luck-chance;anti-cheat-timer;matched-pixels;physical-XR"
    };

    internal NativeCasinoMenu(FalloutCasinoTable table, uint menuId, Func<string, Texture2D?> texture, Action<string> sound,
        Action close, Action<Exception> failed)
    {
        _table = table; MenuId = menuId; _texture = texture; _sound = sound; _close = close; _failed = failed;
        switch (table.Request.Game)
        {
            case FalloutCasinoGame.Blackjack: _blackjack = new(table); break;
            case FalloutCasinoGame.Roulette: _roulette = new(table); break;
            case FalloutCasinoGame.SlotMachine: _slots = new(table); break;
            default: throw new NotSupportedException("Unowned casino game.");
        }
        _bet = table.Request.MinimumBet;
        Name = Game + "Menu"; ProcessMode = ProcessModeEnum.Always; MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new(.03f, .05f, .03f, .98f), MouseFilter = MouseFilterEnum.Stop, AnchorRight = 1, AnchorBottom = 1 });
        var margin = new MarginContainer(); margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 22);
        AddChild(margin);
        _content = new VBoxContainer(); _content.AddThemeConstantOverride("separation", 10); margin.AddChild(_content);
        Theme = new Theme { DefaultFontSize = 18 };
        SetMeta("opennv_menu_id", menuId);
        SetMeta("opennv_ui_source", "Show" + Game + "MenuParams;CSNO;CHIP;shared-inventory;casino-winnings");
        SetMeta("opennv_ui_unverified", "matched-layout-pixels;slot-payout-table;physical-headset");
        if (table.Banned) _message = "You have been banned from gambling at this casino.";
        else if (table.Chips < table.Request.MinimumBet) _message = "You must purchase chips before you can play this game.";
        try { Rebuild(); }
        catch { Free(); throw; }
    }

    private string GameMessage => _blackjack?.Message ?? _roulette?.Message ?? _slots!.Message;
    private bool HandInPlay => _blackjack?.Current == FalloutBlackjackGame.Phase.PlayerTurn;
    private bool CanBet => !_faulted && !_table.Banned && _table.Chips >= _table.Request.MinimumBet;

    private static Button Button(string name, string text, Action pressed, bool disabled = false)
    {
        var button = new Button { Name = name, Text = text, FocusMode = FocusModeEnum.All, CustomMinimumSize = new(0, 38), Disabled = disabled };
        button.Pressed += pressed; return button;
    }

    private void Rebuild()
    {
        foreach (var child in _content.GetChildren()) { _content.RemoveChild(child); child.QueueFree(); }
        var title = new Label { Text = $"{_table.Casino.Name.ToUpperInvariant()} — {Game switch { FalloutCasinoGame.Blackjack => "BLACKJACK", FalloutCasinoGame.Roulette => "ROULETTE", _ => "SLOTS" }}", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 26); _content.AddChild(title);
        _content.AddChild(new Label
        {
            Name = "CasinoStatus",
            Text = $"Chips: {_table.Chips}    Earnings: {_table.Earnings}    Current Bet: {_bet}    Limits: {_table.Request.MinimumBet}–{_table.Request.MaximumBet}",
        });
        if (_blackjack is not null) BuildBlackjack(_blackjack);
        else if (_roulette is not null) BuildRoulette(_roulette);
        else BuildSlots(_slots!);
        _content.AddChild(new Label { Name = "CasinoMessage", Text = _message ?? GameMessage, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var exit = Button("CasinoExit", "Exit [Esc]", Close, HandInPlay);
        exit.SetMeta("opennv_source_choice_goodbye", true);
        _content.AddChild(exit);
        if (IsInsideTree())
            Callable.From(() =>
            {
                if (IsInsideTree() && !_closing && GetViewport().GuiGetFocusOwner() is null)
                    _content.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(button => !button.Disabled)?.GrabFocus();
            }).CallDeferred();
    }

    private HBoxContainer BetControls(bool disabled)
    {
        var row = new HBoxContainer { Name = "CasinoBetControls" };
        int Step() => _bet < 10 ? 1 : _bet < 50 ? 5 : _bet < 200 ? 25 : 100;
        row.AddChild(Button("CasinoDecreaseBet", "Decrease Bet", () => ChangeBet(_bet - Step(), "Decrease"), disabled));
        row.AddChild(Button("CasinoIncreaseBet", "Increase Bet", () => ChangeBet(_bet + Step(), "Increase"), disabled));
        row.AddChild(Button("CasinoBetMax", "Bet Max", () => ChangeBet(_table.MaximumBet, "Increase"), disabled));
        return row;
    }

    private void ChangeBet(int value, string direction)
    {
        _bet = Math.Clamp(value, _table.Request.MinimumBet, Math.Max(_table.Request.MinimumBet, _table.MaximumBet));
        _sound(Game switch
        {
            FalloutCasinoGame.Blackjack => "GAMEBlackJack" + direction + "Bet",
            FalloutCasinoGame.Roulette => "GAMERoulette" + direction + "Bet",
            _ => direction == "Increase" ? "GAMESlotsIncreaseBet01" : "GAMESlotsDecreaseBet",
        });
        _message = null; Rebuild();
    }

    private HFlowContainer Cards(string name, IReadOnlyList<FalloutBlackjackGame.Card> cards, bool hideSecond)
    {
        var row = new HFlowContainer { Name = name };
        var back = _table.Casino.DeckBackTextures[0];
        for (var index = 0; index < cards.Count; ++index)
        {
            var card = cards[index];
            var hidden = hideSecond && index == 1;
            // Faces sit beside the CSNO back: card<deck>_<suit><rank>.dds.
            var face = back.Replace("_Back", "_" + char.ToLowerInvariant("CDHS"[card.Suit]) + card.Rank.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
            var texture = _texture(hidden ? back : face);
            if (texture is null) row.AddChild(new Label { Text = hidden ? "[??]" : $"[{card}]" });
            else row.AddChild(new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new(90, 126),
                TooltipText = hidden ? "Hidden" : card.ToString() });
        }
        return row;
    }

    private void BuildBlackjack(FalloutBlackjackGame game)
    {
        var dealerTotal = game.DealerRevealed ? FalloutBlackjackGame.Total(game.Dealer, out _).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        _content.AddChild(new Label { Text = game.Dealer.Count == 0 ? "Dealer" : $"Dealer: {dealerTotal}" });
        _content.AddChild(Cards("DealerCards", game.Dealer, !game.DealerRevealed));
        for (var index = 0; index < game.Hands.Count; ++index)
        {
            var hand = game.Hands[index];
            _content.AddChild(new Label { Text = $"{(index == game.ActiveHand && HandInPlay ? "▶ " : "")}Hand {index + 1}: {hand.Total}   Bet: {hand.Stake}" });
            _content.AddChild(Cards($"PlayerCards{index}", hand.Cards, false));
        }
        if (!HandInPlay)
        {
            _content.AddChild(BetControls(!CanBet));
            _content.AddChild(Button("BlackjackDeal", "Deal", () => Run(() =>
            {
                game.Deal(_bet);
                if (game.Shuffled) _sound("GAMEBlackJackCardShuffle");
                _sound("GAMEBlackJackCardDeal");
                FinishedSound(game.Current == FalloutBlackjackGame.Phase.Finished, game.LastReturn, game.Hands.Sum(hand => hand.Stake));
            }), !CanBet));
            return;
        }
        var actions = new HFlowContainer { Name = "BlackjackActions" };
        void Action(string name, string text, Action act, bool enabled) => actions.AddChild(Button(name, text, () => Run(() =>
        {
            act();
            _sound("GAMEBlackJackCardDeal");
            FinishedSound(game.Current == FalloutBlackjackGame.Phase.Finished, game.LastReturn, game.Hands.Sum(hand => hand.Stake));
        }), !enabled));
        Action("BlackjackHit", "Hit", game.Hit, true);
        Action("BlackjackStay", "Stay", game.Stay, true);
        Action("BlackjackDoubleDown", "Double Down", game.DoubleDown, game.CanDouble);
        Action("BlackjackSplit", "Split", game.Split, game.CanSplit);
        Action("BlackjackSurrender", "Surrender", game.Surrender, game.CanSurrender);
        _content.AddChild(actions);
    }

    private void FinishedSound(bool finished, long returned, int staked)
    {
        if (!finished) return;
        _sound(Game == FalloutCasinoGame.Blackjack ? returned > staked ? "GAMEBlackJackWin" : "GAMEBlackJackLose" :
            Game == FalloutCasinoGame.Roulette ? returned > staked ? "GAMERouletteWin" : "GAMERouletteInvalidBet" :
            _slots!.LastMultiplier >= FalloutSlotMachineGame.ThreeWildReturn ? "GAMESlotsWinJackpot" :
            _slots.LastMultiplier >= 6 ? "GAMESlotsWinMed" : _slots.LastMultiplier > 1 ? "GAMESlotsWinSmall" : "GAMESlotsLose");
    }

    private void BuildRoulette(FalloutRouletteGame game)
    {
        if (game.LastPocket is { } pocket)
            _content.AddChild(new Label { Name = "RoulettePocket", Text = "Last spin: " + FalloutRouletteGame.Name(pocket) });
        _content.AddChild(BetControls(!CanBet));
        var modes = new HFlowContainer { Name = "RouletteModes" };
        foreach (var (mode, text) in new[] { ("straight", "Straight"), ("split-right", "Split →"), ("split-down", "Split ↓"),
                     ("street", "Street"), ("corner", "Corner"), ("line", "Six Line") })
        {
            var button = Button("RouletteMode_" + mode, text, () => { _rouletteMode = mode; _message = null; Rebuild(); });
            button.ToggleMode = true; button.ButtonPressed = _rouletteMode == mode; modes.AddChild(button);
        }
        _content.AddChild(modes);
        void Place(IReadOnlyList<int> pockets, string label) => Run(() => { game.Place(pockets, _bet, label); _sound("GAMERouletteValidBet"); });
        var zeros = new HBoxContainer();
        zeros.AddChild(Button("RoulettePocket_0", "0", () => Place([0], "0"), !CanBet));
        zeros.AddChild(Button("RoulettePocket_00", "00", () => Place([FalloutRouletteGame.DoubleZero], "00"), !CanBet));
        zeros.AddChild(Button("RouletteFiveNumber", "0,00,1,2,3", () => Place([0, FalloutRouletteGame.DoubleZero, 1, 2, 3], "0,00,1,2,3"), !CanBet));
        _content.AddChild(zeros);
        var grid = new GridContainer { Name = "RouletteGrid", Columns = 12 };
        for (var row = 2; row >= 0; --row)
            for (var column = 0; column < 12; ++column)
            {
                var number = column * 3 + row + 1;
                var button = Button($"RoulettePocket_{number}", number.ToString(System.Globalization.CultureInfo.InvariantCulture), () =>
                {
                    var pockets = _rouletteMode switch
                    {
                        "split-right" => new[] { number, number + 1 },
                        "split-down" => [number, number + 3],
                        "street" => [number - (number - 1) % 3, number - (number - 1) % 3 + 1, number - (number - 1) % 3 + 2],
                        "corner" => [number, number + 1, number + 3, number + 4],
                        "line" => Enumerable.Range(number - (number - 1) % 3, 6).ToArray(),
                        _ => [number],
                    };
                    Place(pockets, string.Join(",", pockets));
                }, !CanBet);
                button.CustomMinimumSize = new(52, 38);
                button.AddThemeColorOverride("font_color", FalloutRouletteGame.IsRed(number) ? new Color(1, .35f, .3f) : Colors.White);
                grid.AddChild(button);
            }
        _content.AddChild(grid);
        var outside = new HFlowContainer { Name = "RouletteOutside" };
        foreach (var (kind, text) in new[] { ("dozen1", "First Dozen"), ("dozen2", "2nd Dozen"), ("dozen3", "3rd Dozen"),
                     ("column1", "2 to 1 (1st)"), ("column2", "2 to 1 (2nd)"), ("column3", "2 to 1 (3rd)"),
                     ("red", "Red"), ("black", "Black"), ("even", "Even"), ("odd", "Odd") })
            outside.AddChild(Button("RouletteBet_" + kind, text, () => Place(FalloutRouletteGame.Outside(kind), text), !CanBet));
        _content.AddChild(outside);
        _content.AddChild(new Label { Text = "Bets: " + (game.Bets.Count == 0 ? "none" : string.Join("  ", game.Bets.Select(bet => $"{bet.Label}×{bet.Amount}"))) +
            $"   Total Bet: {game.TotalBet}", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var actions = new HBoxContainer();
        actions.AddChild(Button("RouletteRemoveBet", "Remove Bet", () => Run(game.RemoveLast), game.Bets.Count == 0));
        actions.AddChild(Button("RouletteSpin", "Spin", () => Run(() =>
        {
            var staked = game.TotalBet;
            _sound("GAMERouletteSpin");
            game.Spin();
            FinishedSound(true, game.LastReturn, staked);
        }), game.Bets.Count == 0 || _faulted));
        _content.AddChild(actions);
    }

    private void BuildSlots(FalloutSlotMachineGame game)
    {
        var reels = new HBoxContainer { Name = "SlotReels", Alignment = BoxContainer.AlignmentMode.Center };
        foreach (var symbol in game.Symbols)
        {
            var texture = _texture(_table.Casino.ReelSymbolTextures[symbol]);
            if (texture is null) reels.AddChild(new Label { Text = symbol == FalloutSlotMachineGame.Wild ? "[W]" : $"[{symbol + 1}]" });
            else reels.AddChild(new TextureRect { Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new(140, 140) });
        }
        _content.AddChild(reels);
        _content.AddChild(BetControls(!CanBet));
        _content.AddChild(Button("SlotSpin", "Spin", () => Run(() =>
        {
            _sound("GameSlotsPullLever");
            game.Spin(_bet);
            FinishedSound(true, game.LastReturn, _bet);
        }), !CanBet));
        _content.AddChild(new Label
        {
            Name = "SlotPayoutList",
            Text = "Payout List (returns per chip): three wilds ×" + FalloutSlotMachineGame.ThreeWildReturn + "; three of a kind (wild substitutes) " +
                string.Join(", ", FalloutSlotMachineGame.ThreeOfAKindReturn.Select((value, index) => $"#{index + 1} ×{value}")),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
    }

    private void Run(Action action)
    {
        try
        {
            action(); _message = null;
            if (_table.Banned) _message = "You have been banned from gambling at this casino.";
            _bet = Math.Clamp(_bet, _table.Request.MinimumBet, Math.Max(_table.Request.MinimumBet, _table.MaximumBet));
            Rebuild();
        }
        catch (Exception error)
        {
            _message = error.Message;
            if (error is not InvalidOperationException) { _faulted = true; _failed(error); }
            Rebuild();
        }
    }

    private void Close()
    {
        if (_closing) return;
        if (HandInPlay) { _message = "Finish the current hand first."; Rebuild(); return; }
        _closing = true; _close();
    }

    public override void _EnterTree() => _viewportLayout = new(this, Layout);
    public override void _Ready() => Layout();
    public override void _ExitTree()
    {
        _viewportLayout?.Dispose();
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) _close();
        }).CallDeferred();
    }
    private void Layout()
    {
        if (!IsInsideTree()) return;
        var scale = GetViewportRect().Size.Y / 900;
        Scale = Vector2.One * scale; Size = GetViewportRect().Size / scale;
    }
    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } || inputEvent.IsActionPressed("ui_cancel"))
        { Close(); GetViewport().SetInputAsHandled(); }
    }
}
