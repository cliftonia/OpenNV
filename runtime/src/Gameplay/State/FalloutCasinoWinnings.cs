using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Gameplay.State;

internal sealed record FalloutCasinoWinningsEntry(FalloutFormKey Casino, long Earnings, int Level);
internal sealed record FalloutCasinoWinningsSnapshot(IReadOnlyList<FalloutCasinoWinningsEntry> Casinos);

// Per-casino net chip earnings and the winnings level that source quests read
// through GetCasinoWinningsLevel (0..4; 4 is the ban level the casino scripts
// test). The level only rises from play; SetCasinoWinningsLevel replaces it.
// Quarter thresholds of the casino maximum winnings are a first-party rule:
// the executable's own thresholds are unverified.
internal sealed class FalloutCasinoWinnings(FalloutPluginStack records)
{
    internal const int BannedLevel = 4;
    private readonly Dictionary<FalloutFormKey, (long Earnings, int Level)> _casinos = [];
    internal long Revision { get; private set; }

    internal long Earnings(FalloutFormKey casino) => _casinos.GetValueOrDefault(Require(casino)).Earnings;
    internal int Level(FalloutFormKey casino) => _casinos.GetValueOrDefault(Require(casino)).Level;
    internal bool Banned(FalloutFormKey casino) => Level(casino) >= BannedLevel;

    internal static int LevelFor(long earnings, int maxWinnings)
    {
        if (maxWinnings <= 0) throw new ArgumentOutOfRangeException(nameof(maxWinnings));
        if (earnings <= 0) return 0;
        return (int)Math.Min(BannedLevel, earnings * BannedLevel / maxWinnings);
    }

    // Returns the level after this result so callers can react to a ban.
    internal int Record(FalloutFormKey casino, long net, int maxWinnings)
    {
        var key = Require(casino);
        var (earnings, level) = _casinos.GetValueOrDefault(key);
        earnings = checked(earnings + net);
        level = Math.Max(level, LevelFor(earnings, maxWinnings));
        _casinos[key] = (earnings, level);
        ++Revision;
        return level;
    }

    internal void SetLevel(FalloutFormKey casino, int level)
    {
        if (level is < 0 or > BannedLevel) throw new InvalidDataException("Casino winnings level must lie in 0..4.");
        var key = Require(casino);
        _casinos[key] = (_casinos.GetValueOrDefault(key).Earnings, level);
        ++Revision;
    }

    internal FalloutCasinoWinningsSnapshot Capture() => new(_casinos
        .OrderBy(entry => entry.Key.OwnerPlugin, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Key.ObjectId)
        .Select(entry => new FalloutCasinoWinningsEntry(entry.Key, entry.Value.Earnings, entry.Value.Level)).ToArray());

    // A save without this owner predates casino play, so every casino starts
    // at zero earnings and level.
    internal void Restore(FalloutCasinoWinningsSnapshot? snapshot)
    {
        var replacement = new Dictionary<FalloutFormKey, (long, int)>();
        foreach (var entry in snapshot?.Casinos ?? [])
        {
            if (entry is null || entry.Level is < 0 or > BannedLevel || !replacement.TryAdd(Require(entry.Casino), (entry.Earnings, entry.Level)))
                throw new InvalidDataException("Saved casino winnings have an invalid or duplicated casino entry.");
        }
        _casinos.Clear();
        foreach (var (casino, value) in replacement) _casinos.Add(casino, value);
        ++Revision;
    }

    private FalloutFormKey Require(FalloutFormKey casino)
    {
        if (string.IsNullOrWhiteSpace(casino.OwnerPlugin) || casino.ObjectId is 0 or > FalloutFormKey.ObjectIdMask)
            throw new InvalidDataException("Casino winnings identity is invalid.");
        var record = records.GetEffective(casino);
        return record.Signature == "CSNO" ? record.FormKey : throw new InvalidDataException("Casino winnings identity is not CSNO.");
    }
}
