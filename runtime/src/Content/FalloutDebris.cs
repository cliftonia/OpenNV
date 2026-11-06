namespace OpenNV.Runtime.Content;

internal sealed record FalloutDebrisModel(byte Percentage, string Model, byte Flags);

// DEBR: weighted model entries. Each DATA holds the percentage weight, a
// mesh path and a flags byte; MODT texture hashes are presentation-only.
internal sealed record FalloutDebris(FalloutFormKey Form, IReadOnlyList<FalloutDebrisModel> Models)
{
    internal static FalloutDebris Read(FalloutPluginStack records, FalloutFormKey form)
    {
        var record = records.GetEffective(form);
        if (record.Signature != "DEBR") throw new InvalidDataException($"{form} is not DEBR.");
        var models = new List<FalloutDebrisModel>();
        foreach (var field in record.ReadSubrecords().Where(field => field.Signature == "DATA"))
        {
            var data = field.Data.Span;
            if (data.Length < 3 || data[^2] != 0)
                throw new InvalidDataException($"Debris {form} has an invalid model entry.");
            var model = FalloutPlugin.DecodeZeroTerminated(data[1..^1], "debris model").Replace('\\', '/');
            if (model.Length == 0 || model.Contains(':') || model.StartsWith('/') || model.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException($"Debris {form} model leaves the owned namespace.");
            if (!model.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) model = "meshes/" + model;
            models.Add(new(data[0], model, data[^1]));
        }
        if (models.Count == 0 || models.All(model => model.Percentage == 0))
            throw new InvalidDataException($"Debris {form} has no weighted model.");
        return new(form, models);
    }

    // Each entry's percentage is its weight.
    internal FalloutDebrisModel Pick(Random random)
    {
        var roll = random.Next(Models.Sum(model => model.Percentage));
        foreach (var model in Models)
        {
            if (roll < model.Percentage) return model;
            roll -= model.Percentage;
        }
        throw new InvalidOperationException("Debris weight selection overran its entries.");
    }
}
