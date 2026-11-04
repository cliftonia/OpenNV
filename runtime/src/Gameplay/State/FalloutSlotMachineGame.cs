using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

// Three-reel slot machine. Each reel has fourteen stops, laid out from the
// CSNO reel-stop counts (six symbols then the wild). The wild substitutes for
// any symbol. The executable's payout list is unverified: the first-party
// table pays three of a kind and three wilds (about 90% return when every
// symbol has two stops).
internal sealed class FalloutSlotMachineGame
{
    internal const int Wild = 6;
    internal static readonly IReadOnlyList<int> ThreeOfAKindReturn = [3, 4, 5, 6, 8, 11];
    internal const int ThreeWildReturn = 50;
    private readonly FalloutCasinoTable _table;
    internal IReadOnlyList<int> Reel { get; }
    internal int[] Stops { get; } = [0, 0, 0];
    internal IReadOnlyList<int> Symbols => Stops.Select(stop => Reel[stop]).ToArray();
    internal string Message { get; private set; } = "Place your bet and spin.";
    internal long LastReturn { get; private set; }
    internal int LastMultiplier { get; private set; }

    internal FalloutSlotMachineGame(FalloutCasinoTable table)
    {
        _table = table;
        // Interleave symbols so equal stops are spread around the reel.
        var remaining = table.Casino.ReelStops.ToArray();
        var reel = new List<int>();
        while (reel.Count < FalloutCasino.ReelStopTotal)
            for (var symbol = 0; symbol < remaining.Length; ++symbol)
                if (remaining[symbol] > 0) { reel.Add(symbol); --remaining[symbol]; }
        Reel = reel;
    }

    internal static int Multiplier(IReadOnlyList<int> symbols)
    {
        if (symbols.Count != 3) throw new ArgumentException("A slot result has three symbols.", nameof(symbols));
        var natural = symbols.Where(symbol => symbol != Wild).Distinct().ToArray();
        return natural.Length switch
        {
            0 => ThreeWildReturn,
            1 => ThreeOfAKindReturn[natural[0]],
            _ => 0,
        };
    }

    internal void Spin(int bet)
    {
        _table.RequireStake(bet);
        _table.Stake(bet);
        void Roll() { for (var reel = 0; reel < 3; ++reel) Stops[reel] = (int)_table.Random.NextBounded((uint)Reel.Count); }
        Roll();
        var replayed = false;
        if (_table.LuckReplays(Multiplier(Symbols) > 1))
        {
            var before = Multiplier(Symbols) > 1;
            Roll();
            replayed = Multiplier(Symbols) > 1 != before;
        }
        LastMultiplier = Multiplier(Symbols);
        LastReturn = (long)bet * LastMultiplier;
        var outcome = LastReturn > bet ? $"You win {LastReturn - bet} chips." : $"You lose {bet} chips.";
        if (replayed) outcome = (LastReturn > bet ? "You feel lucky. " : "You feel unlucky. ") + outcome;
        Message = outcome;
        _table.Settle(bet, LastReturn);
    }
}
