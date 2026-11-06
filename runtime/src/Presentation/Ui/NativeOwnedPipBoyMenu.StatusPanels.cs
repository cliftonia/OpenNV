using System.Globalization;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Presentation.Ui;

internal sealed partial class NativeOwnedPipBoyMenu
{
    private void BindRadiationPanel(GameplayVitals vitals)
    {
        Bind("StatsMenu", "user1", FalloutActorValueStage.DisplayRads(vitals.RadiationRads));
        Bind("StatsMenu", "user2", FalloutActorValueStage.MenuMaximum(_records));
        _tiles.BindText(_tiles.Root, "_rads", Setting("sHUDRads").ToUpperInvariant());
        var resistance = (_actorValue ?? throw new NotSupportedException("RAD panel has no actual RadResist owner."))("RadResist");
        var maximum = _records.NumericSettings.Float("fPlayerMaxResistance");
        if (!float.IsFinite(resistance) || !float.IsFinite(maximum) || maximum < int.MinValue || (double)maximum > int.MaxValue ||
            (double)MathF.Floor(resistance) < int.MinValue || (double)MathF.Floor(resistance) > int.MaxValue)
            throw new InvalidDataException("RAD panel resistance exceeds source integer storage.");
        var shown = Math.Min((int)MathF.Floor(resistance), (int)maximum);
        Text("stats_rad_resist_level", shown.ToString(CultureInfo.InvariantCulture) + "%");
    }

    // H2O, FOD and SLP pages: StatsMenu user11..13 carry the need values and
    // user2 the scale of their stage records. The page label uses the need's
    // AVIF name, upper-cased like the RAD page; that source is unverified.
    private void BindNeedPanel(GameplayVitals vitals, int mode)
    {
        var (trait, actorValue, stage, label, avif) = mode switch
        {
            3 => ("user11", 73, "DEHY", "_h20", "AVDehydration"),
            4 => ("user12", 74, "HUNG", "_hunger", "AVHunger"),
            5 => ("user13", 75, "SLPD", "_sleep", "AVSleepDeprevation"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        Bind("StatsMenu", trait, MathF.Floor(vitals.Need(actorValue)));
        Bind("StatsMenu", "user2", FalloutActorValueStage.MenuMaximum(_records, stage));
        _tiles.BindText(_tiles.Root, label, NameOf(FalloutDialogueTopic.Find(_records, "AVIF", avif).FormKey).ToUpperInvariant());
        SetMeta("opennv_pipboy_need_label_unverified", true);
    }

    private void BindEffectsPanel()
    {
        throw new NotSupportedException("EFF source list completeness requires its ability/equipment/ingestible/poison visibility owner.");
    }
}
