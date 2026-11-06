using System.Buffers.Binary;

namespace OpenNV.Runtime.Content;

// One active-effect instance on an actor: the magic item (SPEL, ENCH, ALCH,
// INGR) that applied it and its base magic effect (MGEF).
internal readonly record struct FalloutActiveEffect(FalloutFormKey Source, FalloutFormKey Effect)
{
    // Every effect a magic item declares, conditioned or not, in source order.
    internal static IEnumerable<FalloutActiveEffect> Declared(FalloutPluginStack records, FalloutFormKey source)
    {
        var record = records.GetEffective(source);
        if (record.Signature is not ("SPEL" or "ENCH" or "ALCH" or "INGR"))
            throw new InvalidDataException($"Active-effect source {source} is not a magic item.");
        foreach (var field in record.ReadSubrecords().Where(field => field.Signature == "EFID"))
        {
            if (field.Data.Length != 4) throw new InvalidDataException($"Magic item {source} has an invalid EFID.");
            var effect = record.Plugin.AdjustFormId(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.Span));
            yield return new(source, effect);
        }
    }
}
