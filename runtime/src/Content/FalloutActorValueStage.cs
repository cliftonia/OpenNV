using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenNV.Runtime.Content;

/// <summary>
/// RADS, DEHY, HUNG and SLPD stage declarations: DATA is {threshold, spell}.
/// Each type is ordered by threshold, highest first; a value's stage is the
/// first entry whose threshold does not exceed it.
/// </summary>
internal sealed record FalloutActorValueStage(FalloutFormKey Form, uint Threshold, FalloutFormKey? Spell)
{
    internal const string Radiation = "RADS";
    private static readonly ConditionalWeakTable<FalloutPluginStack, Dictionary<string, FalloutActorValueStage[]>> Stages = new();

    internal static IReadOnlyList<FalloutActorValueStage> Read(FalloutPluginStack records, string signature = Radiation)
    {
        if (signature is not (Radiation or "DEHY" or "HUNG" or "SLPD")) throw new ArgumentOutOfRangeException(nameof(signature));
        var cache = Stages.GetValue(records, _ => new(StringComparer.Ordinal));
        lock (cache)
        {
            if (cache.TryGetValue(signature, out var cached)) return cached;
            var stages = records.EffectiveRecords(signature).Select(record =>
            {
                var data = record.ReadSubrecords().Single(field => field.Signature == "DATA").Data;
                if (data.Length != 8) throw new InvalidDataException($"{signature} DATA extent is not eight bytes.");
                var threshold = BinaryPrimitives.ReadUInt32LittleEndian(data.Span);
                var spell = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[4..]);
                return new FalloutActorValueStage(record.FormKey, threshold, spell == 0 ? null : record.Plugin.AdjustFormId(spell));
            }).OrderByDescending(stage => stage.Threshold).ToArray();
            cache.Add(signature, stages);
            return stages;
        }
    }

    internal static FalloutActorValueStage? Select(IReadOnlyList<FalloutActorValueStage> stages, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Staged actor value is not finite.");
        foreach (var stage in stages)
            if (stage.Threshold <= value) return stage;
        return null;
    }

    internal static float MenuMaximum(FalloutPluginStack records, string signature = Radiation)
    {
        var stages = Read(records, signature);
        var maximum = stages.Count == 0 ? 1000u : stages[0].Threshold;
        if (maximum == 0 || maximum > int.MaxValue)
            throw new NotSupportedException("Stage menu scale has no positive signed threshold.");
        return maximum;
    }

    internal static float DisplayRads(float rads)
    {
        if (!float.IsFinite(rads)) throw new InvalidDataException("Radiation is not finite.");
        rads = Math.Max(0, rads);
        return rads is > 0 and < 1 ? 1 : (float)Math.Floor((double)rads + .5);
    }
}
