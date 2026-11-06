using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

// Shared stake/settlement owner for the three casino games. Chips move through
// the caller's inventory owner; earnings move through the shared winnings
// owner. A settled result is committed before its completion callback.
internal sealed class FalloutCasinoTable
{
    // Each point of Luck away from 5 gives this chance to replay an outcome in
    // the player's favour (or against them below 5). The retail game prints
    // sLuckyWinText/sUnluckyLoseText for such results; its exact chance is
    // unverified.
    internal const double LuckChancePerPoint = .04;
    private readonly FalloutCasinoWinnings _winnings;
    private readonly Func<int> _chips;
    private readonly Action<int> _changeChips;
    private readonly Action<bool>? _completed;
    internal FalloutCasinoCommandRequest Request { get; }
    internal FalloutSoundRandomState Random { get; }
    internal int Luck { get; }

    internal FalloutCasinoTable(FalloutCasinoCommandRequest request, FalloutCasinoWinnings winnings, Func<int> chips,
        Action<int> changeChips, int luck, ulong seed, Action<bool>? completed = null)
    {
        Request = request; _winnings = winnings; _chips = chips; _changeChips = changeChips; _completed = completed;
        Luck = Math.Clamp(luck, 1, 10); Random = new(seed);
    }

    internal FalloutCasino Casino => Request.Casino;
    internal int Chips => _chips();
    internal long Earnings => _winnings.Earnings(Casino.Form);
    internal bool Banned => _winnings.Banned(Casino.Form);
    internal int MaximumBet => Math.Min(Request.MaximumBet, Chips);

    // Validates a new stake against the source limits and current chips.
    internal void RequireStake(int amount)
    {
        if (Banned) throw new InvalidOperationException("You have been banned from gambling at this casino.");
        if (amount < Request.MinimumBet || amount > Request.MaximumBet)
            throw new InvalidOperationException($"Bets must be between {Request.MinimumBet} and {Request.MaximumBet} chips.");
        if (amount > Chips) throw new InvalidOperationException("You do not have enough chips for that bet.");
    }

    internal void Stake(int amount)
    {
        if (amount <= 0 || amount > Chips) throw new InvalidOperationException("You do not have enough chips for that bet.");
        _changeChips(-amount);
    }

    // Returns chips for a finished round and records the net result.
    internal void Settle(int staked, long returned)
    {
        if (staked < 0 || returned < 0) throw new ArgumentOutOfRangeException(nameof(returned));
        if (returned > 0) _changeChips(checked((int)returned));
        _winnings.Record(Casino.Form, returned - staked, Request.MaxWinnings);
        _completed?.Invoke(returned > staked);
    }

    // The settlement line shared by every game; a Luck replay that flipped the
    // outcome is announced before it.
    internal static string OutcomeText(long staked, long returned, bool replayed = false)
    {
        var outcome = returned > staked ? $"You win {returned - staked} chips." :
            returned == staked ? "You break even." : $"You lose {staked - returned} chips.";
        return replayed ? (returned > staked ? "You feel lucky. " : "You feel unlucky. ") + outcome : outcome;
    }

    // True when a favourable (Luck above 5) or unfavourable (below 5) replay
    // applies to an outcome the player respectively lost or won.
    internal bool LuckReplays(bool playerWon)
    {
        var points = playerWon ? 5 - Luck : Luck - 5;
        return points > 0 && Random.NextUnitFloat() < points * LuckChancePerPoint;
    }
}
