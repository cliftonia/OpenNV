namespace OpenNV.Runtime.Gameplay.State;

// Double-zero roulette. The executable's payout list evidences 0 and 00 and
// the inside bets (straight 35:1, split 17:1, street 11:1, corner 8:1, the
// 0-00-1-2-3 line 6:1, six line 5:1) plus dozens/columns at 2:1 and red,
// black, even and odd at 1:1. Pocket 37 is 00.
internal sealed class FalloutRouletteGame
{
    internal const int DoubleZero = 37;
    internal sealed record Bet(IReadOnlyList<int> Pockets, int Amount, string Label);

    private static readonly HashSet<int> RedPockets = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];
    private static readonly HashSet<string> InsideSets = BuildInsideSets();
    private readonly FalloutCasinoTable _table;
    private readonly List<Bet> _bets = [];
    internal IReadOnlyList<Bet> Bets => _bets;
    internal int TotalBet => _bets.Sum(bet => bet.Amount);
    internal int? LastPocket { get; private set; }
    internal string Message { get; private set; } = "Place your bets.";
    internal long LastReturn { get; private set; }

    internal FalloutRouletteGame(FalloutCasinoTable table) => _table = table;

    internal static string Name(int pocket) => pocket == DoubleZero ? "00" : pocket.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static bool IsRed(int pocket) => RedPockets.Contains(pocket);

    internal static int Payout(int pockets) => pockets switch
    {
        1 => 35, 2 => 17, 3 => 11, 4 => 8, 5 => 6, 6 => 5, 12 => 2, 18 => 1,
        _ => throw new InvalidDataException("Roulette bet covers an unpaid number of pockets."),
    };

    private static string Key(IEnumerable<int> pockets) => string.Join(",", pockets.Order());

    private static HashSet<string> BuildInsideSets()
    {
        var sets = new HashSet<string>();
        void Add(params int[] pockets) => sets.Add(Key(pockets));
        foreach (var pocket in Enumerable.Range(0, 38)) Add(pocket);
        for (var number = 1; number <= 36; ++number)
        {
            if (number % 3 != 0) Add(number, number + 1);
            if (number <= 33) Add(number, number + 3);
            if (number % 3 == 1) Add(number, number + 1, number + 2);
            if (number % 3 != 0 && number <= 32) Add(number, number + 1, number + 3, number + 4);
            if (number % 3 == 1 && number <= 31) Add(number, number + 1, number + 2, number + 3, number + 4, number + 5);
        }
        Add(0, DoubleZero); Add(0, 1); Add(0, 2); Add(DoubleZero, 2); Add(DoubleZero, 3);
        Add(0, 1, 2); Add(DoubleZero, 2, 3); Add(0, DoubleZero, 2);
        Add(0, DoubleZero, 1, 2, 3);
        return sets;
    }

    // Outside bets by name; inside bets by their covered pockets.
    internal static IReadOnlyList<int> Outside(string kind) => kind switch
    {
        "red" => [.. RedPockets],
        "black" => [.. Enumerable.Range(1, 36).Where(pocket => !RedPockets.Contains(pocket))],
        "even" => [.. Enumerable.Range(1, 36).Where(pocket => pocket % 2 == 0)],
        "odd" => [.. Enumerable.Range(1, 36).Where(pocket => pocket % 2 == 1)],
        "dozen1" or "dozen2" or "dozen3" => [.. Enumerable.Range(1 + (kind[^1] - '1') * 12, 12)],
        "column1" or "column2" or "column3" => [.. Enumerable.Range(1, 36).Where(pocket => (pocket - 1) % 3 == kind[^1] - '1')],
        _ => throw new InvalidDataException("Unknown outside roulette bet " + kind),
    };

    internal static bool IsLegal(IReadOnlyCollection<int> pockets) => pockets.Count is 12 or 18
        ? new[] { "red", "black", "even", "odd", "dozen1", "dozen2", "dozen3", "column1", "column2", "column3" }
            .Any(kind => Key(Outside(kind)) == Key(pockets))
        : InsideSets.Contains(Key(pockets));

    internal void Place(IReadOnlyList<int> pockets, int amount, string label)
    {
        if (pockets.Distinct().Count() != pockets.Count || !IsLegal(pockets.ToArray()))
            throw new InvalidOperationException("That is not a valid roulette bet.");
        var total = TotalBet + amount;
        if (amount <= 0) throw new InvalidOperationException("Bets must be at least one chip.");
        _table.RequireStake(Math.Max(total, _table.Request.MinimumBet));
        _bets.Add(new(pockets.Order().ToArray(), amount, label));
        Message = $"Total bet: {TotalBet}";
    }

    internal void RemoveLast()
    {
        if (_bets.Count == 0) throw new InvalidOperationException("There is no bet to remove.");
        _bets.RemoveAt(_bets.Count - 1);
        Message = $"Total bet: {TotalBet}";
    }

    internal long Return(int pocket) => _bets.Where(bet => bet.Pockets.Contains(pocket))
        .Sum(bet => (long)bet.Amount * (Payout(bet.Pockets.Count) + 1));

    internal void Spin()
    {
        if (_bets.Count == 0) throw new InvalidOperationException("Place a bet before spinning.");
        var staked = TotalBet;
        _table.RequireStake(staked);
        _table.Stake(staked);
        var pocket = (int)_table.Random.NextBounded(38);
        var replayed = false;
        if (_table.LuckReplays(Return(pocket) > staked))
        {
            var again = (int)_table.Random.NextBounded(38);
            replayed = Return(again) > staked != Return(pocket) > staked;
            pocket = again;
        }
        var returned = Return(pocket);
        LastPocket = pocket; LastReturn = returned;
        Message = $"{Name(pocket)} {(pocket is 0 or DoubleZero ? "green" : IsRed(pocket) ? "red" : "black")}. " +
            FalloutCasinoTable.OutcomeText(staked, returned, replayed);
        _bets.Clear();
        _table.Settle(staked, returned);
    }
}
