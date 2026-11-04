using System.Buffers.Binary;
using System.Text;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.World.Cells;

internal static class CasinoContracts
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("opennv-casino-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "Casino.esm"), Join(
                Record("TES4", 0, Field("HEDR", new byte[12])),
                Record("CHIP", 0x10, Field("EDID", Text("Chip")), Field("FULL", Text("Chip"))),
                Record("QUST", 0x11, Field("EDID", Text("Comps"))),
                Casino(0x20, Data(.2f, 1.5f, [2, 2, 2, 2, 2, 2, 2], 3, 1000, 0x10, 0x11, 0)),
                Casino(0x21, Data(.35f, 1.2f, [2, 2, 2, 2, 2, 2, 2], 1, 400, 0x10, 0x11, 1)),
                Casino(0x22, Data(.2f, 1.5f, [2, 2, 2, 2, 2, 2, 3], 3, 1000, 0x10, 0x11, 0)),
                Casino(0x23, Data(.2f, 1.5f, [2, 2, 2, 2, 2, 2, 2], 3, 1000, 0x11, 0x11, 0))));
            using var stack = FalloutPluginStack.Load(directory, ["Casino.esm"]);
            var gomorrah = FalloutCasino.Read(stack, stack.GetEffective(Key(0x20)));
            Check(gomorrah is { Decks: 3, MaxWinnings: 1000, DealerStaysOnSoft17: false, ShuffleFraction: .2f, BlackjackPayoutRatio: 1.5f } &&
                gomorrah.Chip == Key(0x10) && gomorrah.WinningsQuest == Key(0x11) && gomorrah.ReelStops.Sum() == 14 &&
                gomorrah.ReelSymbolTextures.Count == 7 && gomorrah.DeckBackTextures[0] == @"terminals\Deck\card_Back.dds",
                "CSNO DATA lost its source layout.");
            Check(FalloutCasino.Read(stack, stack.GetEffective(Key(0x21))).DealerStaysOnSoft17, "CSNO soft-17 flag was lost.");
            Reject(() => FalloutCasino.Read(stack, stack.GetEffective(Key(0x22))));
            Reject(() => FalloutCasino.Read(stack, stack.GetEffective(Key(0x23))));
            var request = FalloutCasinoCommandRequest.Read(stack, FalloutCasinoGame.Blackjack, stack.GetEffective(Key(0x20)), 1, 200, 0);
            Check(request.MaxWinnings == 1000 && request.MinimumBet == 1 && request.MaximumBet == 200, "Zero max winnings did not keep CSNO.");
            Check(FalloutCasinoCommandRequest.Read(stack, FalloutCasinoGame.Roulette, stack.GetEffective(Key(0x20)), 5, 50, 300).MaxWinnings == 300,
                "Script max winnings did not override CSNO.");
            Reject(() => FalloutCasinoCommandRequest.Read(stack, FalloutCasinoGame.Roulette, stack.GetEffective(Key(0x20)), 0, 50, 0));
            Reject(() => FalloutCasinoCommandRequest.Read(stack, FalloutCasinoGame.Roulette, stack.GetEffective(Key(0x20)), 60, 50, 0));
            Check(FalloutCasinoCommandRequest.GameFor("showslotmachinemenuparams") == FalloutCasinoGame.SlotMachine, "Slot command was not bound.");

            Winnings(stack);
            Blackjack(stack, request);
            Roulette(stack, request with { Game = FalloutCasinoGame.Roulette });
            Slots(stack, request with { Game = FalloutCasinoGame.SlotMachine, MaximumBet = 60 });
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("OPENNV_CASINO_CONTRACT_PASS csno=true winnings=true scriptLevel=true blackjack=true roulette=true slots=true luck=true saves=true payoutTable=unverified");
    }

    private static void Winnings(FalloutPluginStack stack)
    {
        var winnings = new FalloutCasinoWinnings(stack);
        Check(FalloutCasinoWinnings.LevelFor(249, 1000) == 0 && FalloutCasinoWinnings.LevelFor(250, 1000) == 1 &&
            FalloutCasinoWinnings.LevelFor(999, 1000) == 3 && FalloutCasinoWinnings.LevelFor(5000, 1000) == 4 &&
            FalloutCasinoWinnings.LevelFor(-10, 1000) == 0, "Winnings level thresholds changed.");
        Check(winnings.Record(Key(0x20), 600, 1000) == 2 && winnings.Record(Key(0x20), -500, 1000) == 2 && winnings.Earnings(Key(0x20)) == 100,
            "Winnings level fell after a loss or earnings were not net.");
        var function = FalloutScriptCasinoCommands.Function("getcasinowinningslevel", stack, () => winnings)!;
        var casinoArgument = new FalloutScriptArgument(FalloutScriptValue.Form(stack.RuntimeFormId(Key(0x20))));
        Check(function.Invoke([casinoArgument]) == 2, "GetCasinoWinningsLevel did not read the shared owner.");
        FalloutScriptCasinoCommands.Function("setcasinowinningslevel", stack, () => winnings)!
            .Invoke([casinoArgument, new(4)]);
        Check(winnings.Banned(Key(0x20)) && function.Invoke([casinoArgument]) == 4, "SetCasinoWinningsLevel did not ban.");
        Reject(() => winnings.SetLevel(Key(0x20), 5));
        Reject(() => winnings.Level(Key(0x10)));
        var restored = new FalloutCasinoWinnings(stack);
        restored.Restore(System.Text.Json.JsonSerializer.Deserialize<FalloutCasinoWinningsSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(winnings.Capture())));
        Check(restored.Level(Key(0x20)) == 4 && restored.Earnings(Key(0x20)) == 100, "Casino winnings did not survive a save.");
        restored.Restore(null);
        Check(restored.Level(Key(0x20)) == 0, "A pre-casino save did not start with zero winnings.");
        Reject(() => restored.Restore(new([new(Key(0x20), 0, 1), new(Key(0x20), 0, 1)])));
    }

    private static (FalloutCasinoTable Table, Func<int> Chips) Table(FalloutPluginStack stack, FalloutCasinoCommandRequest request,
        int chips, int luck = 5, ulong seed = 7)
    {
        var wallet = chips;
        var winnings = new FalloutCasinoWinnings(stack);
        var table = new FalloutCasinoTable(request, winnings, () => wallet, delta =>
        {
            if (wallet + delta < 0) throw new InvalidOperationException("Wallet overdrawn.");
            wallet += delta;
        }, luck, seed);
        return (table, () => wallet);
    }

    private static void Blackjack(FalloutPluginStack stack, FalloutCasinoCommandRequest request)
    {
        Check(FalloutBlackjackGame.Total([new(1, 0), new(6, 0)], out var soft) == 17 && soft, "A+6 is not soft 17.");
        Check(FalloutBlackjackGame.Total([new(1, 0), new(6, 0), new(10, 0)], out soft) == 17 && !soft, "A+6+10 is not hard 17.");
        Check(FalloutBlackjackGame.Total([new(1, 0), new(1, 1), new(13, 0)], out _) == 12, "Two aces and a king are not 12.");
        // Every finished round conserves chips: wallet change equals return minus stake.
        for (ulong seed = 1; seed <= 200; ++seed)
        {
            var (table, chips) = Table(stack, request, 1000, seed: seed);
            var game = new FalloutBlackjackGame(table);
            for (var round = 0; round < 20; ++round)
            {
                var before = chips();
                game.Deal(10);
                while (game.Current == FalloutBlackjackGame.Phase.PlayerTurn)
                {
                    var hand = game.Hands[game.ActiveHand];
                    if (game.CanSplit && seed % 3 == 0) game.Split();
                    else if (game.CanDouble && hand.Total is 10 or 11) game.DoubleDown();
                    else if (game.CanSurrender && hand.Total == 16 && seed % 5 == 0) game.Surrender();
                    else if (hand.Total < 17) game.Hit();
                    else game.Stay();
                }
                var staked = game.Hands.Sum(hand => hand.Stake);
                Check(chips() - before == game.LastReturn - staked, "Blackjack settlement did not conserve chips.");
                Check(game.Hands.All(hand => hand.Cards.Count <= FalloutBlackjackGame.MaximumHandSize), "Hand exceeded seven cards.");
                var dealer = FalloutBlackjackGame.Total(game.Dealer, out _);
                Check(game.Hands.All(hand => hand.Total > 21) || game.Dealer.Count == 2 || dealer >= 17 || game.Dealer.Count == 7,
                    "Dealer stood below 17.");
            }
            Check(table.Earnings == chips() - 1000, "Blackjack earnings did not match the net chip change.");
        }
        var (broke, _) = Table(stack, request, 0);
        RejectPlay(() => new FalloutBlackjackGame(broke).Deal(1));
        var (limited, _) = Table(stack, request, 1000);
        RejectPlay(() => new FalloutBlackjackGame(limited).Deal(201));
    }

    private static void Roulette(FalloutPluginStack stack, FalloutCasinoCommandRequest request)
    {
        Check(FalloutRouletteGame.IsLegal([5]) && FalloutRouletteGame.IsLegal([5, 6]) && FalloutRouletteGame.IsLegal([5, 8]) &&
            FalloutRouletteGame.IsLegal([4, 5, 6]) && FalloutRouletteGame.IsLegal([1, 2, 4, 5]) &&
            FalloutRouletteGame.IsLegal([0, FalloutRouletteGame.DoubleZero, 1, 2, 3]) && FalloutRouletteGame.IsLegal([1, 2, 3, 4, 5, 6]) &&
            FalloutRouletteGame.IsLegal(FalloutRouletteGame.Outside("red").ToArray()) && FalloutRouletteGame.IsLegal(FalloutRouletteGame.Outside("column2").ToArray()),
            "A legal roulette bet was rejected.");
        Check(!FalloutRouletteGame.IsLegal([3, 4]) && !FalloutRouletteGame.IsLegal([5, 7]) && !FalloutRouletteGame.IsLegal([3, 4, 6, 7]) &&
            !FalloutRouletteGame.IsLegal([2, 3, 4]), "An illegal roulette bet was admitted.");
        Check(FalloutRouletteGame.Payout(1) == 35 && FalloutRouletteGame.Payout(2) == 17 && FalloutRouletteGame.Payout(3) == 11 &&
            FalloutRouletteGame.Payout(4) == 8 && FalloutRouletteGame.Payout(5) == 6 && FalloutRouletteGame.Payout(6) == 5 &&
            FalloutRouletteGame.Payout(12) == 2 && FalloutRouletteGame.Payout(18) == 1, "Roulette payouts changed.");
        Check(FalloutRouletteGame.Outside("red").Count == 18 && FalloutRouletteGame.Outside("dozen3")[0] == 25 &&
            FalloutRouletteGame.Outside("column1").SequenceEqual(Enumerable.Range(0, 12).Select(row => row * 3 + 1)), "Outside bets changed.");
        var (table, chips) = Table(stack, request, 500, seed: 3);
        var game = new FalloutRouletteGame(table);
        for (var spin = 0; spin < 100; ++spin)
        {
            game.Place([17], 2, "17"); game.Place(FalloutRouletteGame.Outside("black"), 5, "Black");
            var before = chips();
            game.Spin();
            var pocket = game.LastPocket!.Value;
            var expected = (pocket == 17 ? 72 : 0) + (pocket is >= 1 and <= 36 && !FalloutRouletteGame.IsRed(pocket) ? 10 : 0);
            Check(game.LastReturn == expected && chips() - before == expected - 7 && game.Bets.Count == 0, "Roulette spin was not settled exactly.");
        }
        game.Place([1], 1, "1"); game.RemoveLast();
        RejectPlay(game.Spin);
        RejectPlay(() => game.Place([1], 201, "1"));
        RejectPlay(() => game.Place([3, 4], 1, "3,4"));
    }

    private static void Slots(FalloutPluginStack stack, FalloutCasinoCommandRequest request)
    {
        const int W = FalloutSlotMachineGame.Wild;
        Check(FalloutSlotMachineGame.Multiplier([W, W, W]) == FalloutSlotMachineGame.ThreeWildReturn &&
            FalloutSlotMachineGame.Multiplier([2, W, 2]) == FalloutSlotMachineGame.ThreeOfAKindReturn[2] &&
            FalloutSlotMachineGame.Multiplier([0, 1, W]) == 0 && FalloutSlotMachineGame.Multiplier([5, 5, 5]) == 11, "Slot payouts changed.");
        var (table, chips) = Table(stack, request, 5000, seed: 11);
        var game = new FalloutSlotMachineGame(table);
        Check(game.Reel.Count == 14 && Enumerable.Range(0, 7).All(symbol => game.Reel.Count(value => value == symbol) == 2), "Reel ignored CSNO stops.");
        for (var spin = 0; spin < 200; ++spin)
        {
            var before = chips();
            game.Spin(3);
            Check(chips() - before == game.LastReturn - 3 && game.LastReturn == 3L * FalloutSlotMachineGame.Multiplier(game.Symbols),
                "Slot spin was not settled exactly.");
        }
        RejectPlay(() => game.Spin(61));
        // Luck replays: a lucky player wins more often over the same seeds.
        long Total(int luck)
        {
            long sum = 0;
            for (ulong seed = 1; seed <= 300; ++seed)
            {
                var (lucky, wallet) = Table(stack, request, 100, luck, seed);
                new FalloutSlotMachineGame(lucky).Spin(1);
                sum += wallet() - 100;
            }
            return sum;
        }
        Check(Total(10) > Total(5) && Total(5) > Total(1), "Luck did not move slot results in the player's direction.");
        var (capped, _) = Table(stack, request with { MaxWinnings = 10 }, 100, seed: 5);
        var cappedGame = new FalloutSlotMachineGame(capped);
        while (!capped.Banned && capped.Chips >= 1) cappedGame.Spin(Math.Min(60, capped.Chips));
        Check(capped.Banned ? capped.Earnings >= 10 : capped.Chips == 0, "Reaching maximum winnings did not ban.");
        if (capped.Banned) RejectPlay(() => cappedGame.Spin(1));
    }

    private static byte[] Casino(uint id, byte[] data) => Record("CSNO", id, Field("EDID", Text("Casino" + id)), Field("FULL", Text("Casino")),
        Field("DATA", data), Field("MODL", Text("chip.nif")), Field("MOD3", Text("table.nif")),
        Field("ICON", Text("s1.dds")), Field("ICON", Text("s2.dds")), Field("ICON", Text("s3.dds")), Field("ICON", Text("s4.dds")),
        Field("ICON", Text("s6.dds")), Field("ICON", Text("s7.dds")), Field("ICON", Text("s5.dds")), Field("ICO2", Text(@"terminals\Deck\card_Back.dds")));

    private static byte[] Data(float shuffle, float payout, int[] stops, int decks, int maxWinnings, uint chip, uint quest, uint flags)
    {
        var data = new byte[56];
        BinaryPrimitives.WriteSingleLittleEndian(data, shuffle);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), payout);
        for (var index = 0; index < 7; ++index) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + index * 4), stops[index]);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(36), decks);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), maxWinnings);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), chip);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), quest);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52), flags);
        return data;
    }

    private static FalloutFormKey Key(uint id) => new("Casino.esm", id);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
        throw new InvalidDataException("Invalid casino source or state was accepted.");
    }
    private static void RejectPlay(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidDataException("An invalid casino play was accepted.");
    }
    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value + '\0');
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Field(string name, byte[] data)
    {
        var result = new byte[6 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), checked((ushort)data.Length)); data.CopyTo(result, 6); return result;
    }
    private static byte[] Record(string name, uint id, params byte[][] fields)
    {
        var data = Join(fields); var result = new byte[24 + data.Length]; Encoding.ASCII.GetBytes(name).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), id); data.CopyTo(result, 24); return result;
    }
}
