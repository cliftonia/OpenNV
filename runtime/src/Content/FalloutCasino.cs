using System.Buffers.Binary;

namespace OpenNV.Runtime.Content;

internal enum FalloutCasinoGame { Blackjack, Roulette, SlotMachine }

// CSNO source declaration. DATA layout (56 bytes): shuffle fraction, blackjack
// payout ratio, seven slot reel stop counts (six symbols then wild), deck
// count, maximum winnings, CHIP currency, winnings QUST and flags (bit 0:
// dealer stays on soft 17). Field names follow xEdit wbDefinitionsFNV CSNO.
// The executable rejects slot reels whose stop counts do not sum to fourteen.
internal sealed record FalloutCasino(FalloutFormKey Form, string EditorId, string Name,
    float ShuffleFraction, float BlackjackPayoutRatio, IReadOnlyList<int> ReelStops, int Decks,
    int MaxWinnings, FalloutFormKey Chip, FalloutFormKey WinningsQuest, bool DealerStaysOnSoft17,
    IReadOnlyList<string> ReelSymbolTextures, IReadOnlyList<string> DeckBackTextures)
{
    internal const int ReelStopTotal = 14;
    internal const int SymbolCount = 7;

    internal static FalloutCasino Read(FalloutPluginStack records, FalloutPluginRecord record)
    {
        if (record.Signature != "CSNO") throw new InvalidDataException("Casino source is not CSNO.");
        var fields = record.ReadSubrecords().ToArray();
        var unknown = fields.FirstOrDefault(field => field.Signature is not
            ("EDID" or "FULL" or "DATA" or "MODL" or "MODT" or "MOD2" or "MOD3" or "MOD4" or "ICON" or "ICO2"));
        if (unknown.Signature is not null) throw new NotSupportedException("Unowned casino source field: " + unknown.Signature);
        FalloutPluginSubrecord One(string signature)
        {
            var matches = fields.Where(field => field.Signature == signature).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Casino source requires one {signature} field.");
        }
        var data = One("DATA").Data.Span;
        if (data.Length != 56) throw new InvalidDataException("Casino DATA must contain fifty-six bytes.");
        var shuffle = BinaryPrimitives.ReadSingleLittleEndian(data);
        var payout = BinaryPrimitives.ReadSingleLittleEndian(data[4..]);
        if (!float.IsFinite(shuffle) || shuffle is <= 0 or > 1)
            throw new InvalidDataException("Casino shuffle fraction must lie in (0, 1].");
        if (!float.IsFinite(payout) || payout <= 0) throw new InvalidDataException("Casino blackjack payout ratio must be positive.");
        var stops = new int[SymbolCount];
        for (var index = 0; index < SymbolCount; ++index)
            stops[index] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + index * 4)..]));
        if (stops.Sum() != ReelStopTotal)
            throw new InvalidDataException("Casino slot reel stops must sum to fourteen.");
        var decks = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[36..]));
        var maximum = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[40..]));
        if (decks <= 0) throw new InvalidDataException("Casino must deal from at least one deck.");
        if (maximum <= 0) throw new InvalidDataException("Casino maximum winnings must be positive.");
        var chip = record.Plugin.AdjustFormId(BinaryPrimitives.ReadUInt32LittleEndian(data[44..]));
        if (records.GetEffective(chip).Signature != "CHIP") throw new InvalidDataException("Casino currency is not CHIP.");
        var quest = record.Plugin.AdjustFormId(BinaryPrimitives.ReadUInt32LittleEndian(data[48..]));
        if (records.GetEffective(quest).Signature != "QUST") throw new InvalidDataException("Casino winnings quest is not QUST.");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        if ((flags & ~1u) != 0) throw new NotSupportedException($"Casino flags 0x{flags:x} are unowned.");
        string Text(FalloutPluginSubrecord field) => FalloutDialogueTopic.Text(field.Data.Span);
        var symbols = fields.Where(field => field.Signature == "ICON").Select(Text).ToArray();
        if (symbols.Length != SymbolCount) throw new InvalidDataException("Casino must declare seven slot reel symbol textures.");
        var decksTextures = fields.Where(field => field.Signature == "ICO2").Select(Text).ToArray();
        if (decksTextures.Length == 0) throw new InvalidDataException("Casino must declare a blackjack deck texture.");
        var name = fields.SingleOrDefault(field => field.Signature == "FULL");
        return new(record.FormKey, Text(One("EDID")), name.Signature is null ? Text(One("EDID")) : Text(name),
            shuffle, payout, Array.AsReadOnly(stops), decks, maximum, chip, quest, (flags & 1) != 0,
            Array.AsReadOnly(symbols), Array.AsReadOnly(decksTextures));
    }
}

// ShowBlackJackMenuParams / ShowRouletteMenuParams / ShowSlotMachineMenuParams
// declare Casino, Min Bet, Max Bet and Max Winnings in the executable. A zero
// maximum winnings argument keeps the CSNO maximum; this choice is unverified.
internal sealed record FalloutCasinoCommandRequest(FalloutCasinoGame Game, FalloutCasino Casino,
    int MinimumBet, int MaximumBet, int MaxWinnings)
{
    internal static FalloutCasinoGame GameFor(string operation) => operation switch
    {
        "showblackjackmenuparams" => FalloutCasinoGame.Blackjack,
        "showroulettemenuparams" => FalloutCasinoGame.Roulette,
        "showslotmachinemenuparams" => FalloutCasinoGame.SlotMachine,
        _ => throw new NotSupportedException("Unowned casino command " + operation),
    };

    internal static FalloutCasinoCommandRequest Read(FalloutPluginStack records, FalloutCasinoGame game,
        FalloutPluginRecord casino, double minimumBet, double maximumBet, double maxWinnings)
    {
        static int Integer(double value, string label) =>
            double.IsFinite(value) && value == Math.Truncate(value) && value is >= 0 and <= int.MaxValue
                ? (int)value : throw new InvalidDataException($"Casino {label} must be a nonnegative integer.");
        var source = FalloutCasino.Read(records, casino);
        var minimum = Integer(minimumBet, "minimum bet");
        var maximum = Integer(maximumBet, "maximum bet");
        var winnings = Integer(maxWinnings, "maximum winnings");
        if (minimum == 0 || maximum < minimum) throw new InvalidDataException("Casino bet limits must satisfy 0 < min <= max.");
        return new(game, source, minimum, maximum, winnings == 0 ? source.MaxWinnings : winnings);
    }
}
