namespace OpenNV.Runtime.Gameplay.State;

// Blackjack against the casino shoe. Source rules: CSNO deck count, shuffle
// fraction, natural payout ratio and dealer soft-17 flag. Engine strings and
// slot names evidence Deal, Hit, Stay, Double Down, Split (two hands),
// Surrender and a seven-card hand limit. Peek, split and surrender details,
// natural rounding and the shuffle point are first-party and unverified.
internal sealed class FalloutBlackjackGame
{
    internal const int MaximumHandSize = 7;
    internal enum Phase { Betting, PlayerTurn, Finished }
    internal readonly record struct Card(int Rank, int Suit)
    {
        internal int Value => Math.Min(10, Rank);
        public override string ToString() => (Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => Rank.ToString(System.Globalization.CultureInfo.InvariantCulture) }) +
            "CDHS"[Suit];
    }

    internal sealed class Hand
    {
        internal List<Card> Cards { get; } = [];
        internal int Stake { get; set; }
        internal int Total => FalloutBlackjackGame.Total(Cards, out _);
    }

    private readonly FalloutCasinoTable _table;
    private readonly List<Card> _shoe = [];
    private int _next;
    private readonly List<Hand> _hands = [];
    private readonly List<Card> _dealer = [];
    private bool _split, _surrendered;
    internal Phase Current { get; private set; } = Phase.Betting;
    internal IReadOnlyList<Hand> Hands => _hands;
    internal IReadOnlyList<Card> Dealer => _dealer;
    internal int ActiveHand { get; private set; }
    internal bool DealerRevealed => Current != Phase.PlayerTurn;
    internal string Message { get; private set; } = "Place your bet.";
    internal long LastReturn { get; private set; }
    internal int ShoeRemaining => _shoe.Count - _next;
    internal bool Shuffled { get; private set; }

    internal FalloutBlackjackGame(FalloutCasinoTable table)
    {
        _table = table;
        for (var deck = 0; deck < table.Casino.Decks; ++deck)
            for (var suit = 0; suit < 4; ++suit)
                for (var rank = 1; rank <= 13; ++rank) _shoe.Add(new(rank, suit));
        Shuffle();
    }

    // Aces count eleven while that keeps the hand at or under 21 (soft).
    internal static int Total(IReadOnlyList<Card> cards, out bool soft)
    {
        var hard = cards.Sum(card => Math.Min(10, card.Rank));
        soft = cards.Any(card => card.Rank == 1) && hard + 10 <= 21;
        return soft ? hard + 10 : hard;
    }

    private static bool Natural(IReadOnlyList<Card> cards) => cards.Count == 2 && Total(cards, out _) == 21;

    private void Shuffle()
    {
        for (var index = _shoe.Count - 1; index > 0; --index)
        {
            var other = (int)_table.Random.NextBounded((uint)(index + 1));
            (_shoe[index], _shoe[other]) = (_shoe[other], _shoe[index]);
        }
        _next = 0; Shuffled = true;
    }

    private Card Draw()
    {
        if (_next >= _shoe.Count) throw new InvalidOperationException("The blackjack shoe is exhausted.");
        return _shoe[_next++];
    }

    internal bool CanDouble => Current == Phase.PlayerTurn && _hands[ActiveHand].Cards.Count == 2 &&
        _table.Chips >= _hands[ActiveHand].Stake;
    internal bool CanSplit => Current == Phase.PlayerTurn && !_split && _hands[0].Cards.Count == 2 &&
        _hands[0].Cards[0].Value == _hands[0].Cards[1].Value && _table.Chips >= _hands[0].Stake;
    internal bool CanSurrender => Current == Phase.PlayerTurn && !_split && _hands[0].Cards.Count == 2;

    internal void Deal(int bet)
    {
        if (Current == Phase.PlayerTurn) throw new InvalidOperationException("A hand is already in play.");
        _table.RequireStake(bet);
        Shuffled = false;
        // Reshuffle before a round once the source fraction of the shoe is used.
        if (_next >= _shoe.Count * (double)_table.Casino.ShuffleFraction || ShoeRemaining < 2 * MaximumHandSize * 2) Shuffle();
        _table.Stake(bet);
        _hands.Clear(); _dealer.Clear(); _split = false; _surrendered = false; ActiveHand = 0; LastReturn = 0;
        var hand = new Hand { Stake = bet };
        _hands.Add(hand);
        hand.Cards.Add(Draw()); _dealer.Add(Draw()); hand.Cards.Add(Draw()); _dealer.Add(Draw());
        Current = Phase.PlayerTurn;
        if (Natural(hand.Cards) || Natural(_dealer)) { Finish(); return; }
        Message = "Hit, stay, double down" + (CanSplit ? ", split" : "") + " or surrender.";
    }

    internal void Hit()
    {
        RequireTurn();
        var hand = _hands[ActiveHand];
        if (hand.Cards.Count >= MaximumHandSize) throw new InvalidOperationException("You've reached the max hand size.");
        hand.Cards.Add(Draw());
        if (hand.Total >= 21 || hand.Cards.Count == MaximumHandSize) Advance();
        else Message = "Hit or stay.";
    }

    internal void Stay() { RequireTurn(); Advance(); }

    internal void DoubleDown()
    {
        RequireTurn();
        if (!CanDouble) throw new InvalidOperationException("You can only double down on two cards with chips to match your bet.");
        var hand = _hands[ActiveHand];
        _table.Stake(hand.Stake);
        hand.Stake *= 2;
        hand.Cards.Add(Draw());
        Advance();
    }

    internal void Split()
    {
        RequireTurn();
        if (!CanSplit) throw new InvalidOperationException("Only an opening pair of equal value can be split, once.");
        var first = _hands[0];
        _table.Stake(first.Stake);
        var second = new Hand { Stake = first.Stake };
        second.Cards.Add(first.Cards[1]); first.Cards.RemoveAt(1);
        first.Cards.Add(Draw()); second.Cards.Add(Draw());
        _hands.Add(second); _split = true;
        Message = "Playing hand 1 of 2.";
        if (first.Total >= 21) Advance();
    }

    internal void Surrender()
    {
        RequireTurn();
        if (!CanSurrender) throw new InvalidOperationException("You can only surrender your opening two cards.");
        _surrendered = true;
        Finish();
    }

    private void RequireTurn()
    {
        if (Current != Phase.PlayerTurn) throw new InvalidOperationException("No blackjack hand is in play.");
    }

    private void Advance()
    {
        if (ActiveHand + 1 < _hands.Count)
        {
            ++ActiveHand;
            Message = $"Playing hand {ActiveHand + 1} of {_hands.Count}.";
            if (_hands[ActiveHand].Total >= 21) Advance();
            return;
        }
        Finish();
    }

    private void PlayDealer()
    {
        var best = _hands.Where(hand => hand.Total <= 21).Select(hand => hand.Total).DefaultIfEmpty(0).Max();
        while (true)
        {
            var total = Total(_dealer, out var soft);
            if (total > 17 || total == 17 && (!soft || _table.Casino.DealerStaysOnSoft17)) return;
            if (_dealer.Count >= MaximumHandSize) return;
            var card = Draw();
            var after = Total([.. _dealer, card], out _);
            // A lucky player sees a card that would beat them replaced; an
            // unlucky player sees a dealer bust replaced. The card returns to
            // the end of the shoe.
            var dealerWins = after <= 21 && after >= 17 && after > best;
            if (dealerWins && _table.LuckReplays(false) || after > 21 && _table.LuckReplays(true))
            {
                _shoe.RemoveAt(_next - 1); _shoe.Add(card); --_next;
                card = Draw();
            }
            _dealer.Add(card);
        }
    }

    private void Finish()
    {
        Current = Phase.Finished;
        var staked = _hands.Sum(hand => hand.Stake);
        long returned = 0;
        var dealerNatural = Natural(_dealer);
        if (_surrendered) returned = _hands[0].Stake / 2;
        else if (!_split && Natural(_hands[0].Cards))
            returned = dealerNatural ? _hands[0].Stake : _hands[0].Stake + (long)Math.Floor(_hands[0].Stake * (double)_table.Casino.BlackjackPayoutRatio);
        else if (!dealerNatural)
        {
            if (_hands.Any(hand => hand.Total <= 21)) PlayDealer();
            var dealer = Total(_dealer, out _);
            foreach (var hand in _hands)
            {
                var total = hand.Total;
                if (total > 21) continue;
                if (dealer > 21 || total > dealer) returned += hand.Stake * 2L;
                else if (total == dealer) returned += hand.Stake;
            }
        }
        LastReturn = returned;
        Message = _surrendered ? "You surrender half your bet." : FalloutCasinoTable.OutcomeText(staked, returned);
        _table.Settle(staked, returned);
    }
}
