using System.Globalization;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceScripts
{
    // Player-facing commands whose state belongs to the shared world. Commands
    // that need a live menu or presentation owner continue to the host command
    // owner with their numeric operands already evaluated in script scope.
    private bool TryPlayerCommand(FalloutFormKey source, FalloutScriptBindings bindings, string[] parts, string operation,
        FalloutFormKey target, IReadOnlyList<string> arguments, Func<string, double> number, Func<string, FalloutFormKey> reference,
        Func<string, string> text)
    {
        switch (operation)
        {
            case "pipboyradio":
                if (parts.Length != 1 || arguments.Count is < 1 or > 2)
                    throw new InvalidDataException("PipBoyRadio requires on, off or tune with a station reference.");
                PipBoyRadio(arguments, number, reference);
                return true;
            case "showallmapmarkers":
                if (parts.Length != 1 || arguments.Count != 3)
                    throw new InvalidDataException("ShowAllMapMarkers requires visible, travel and hidden flags.");
                var flags = arguments.Select(argument => number(argument) switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new InvalidDataException("ShowAllMapMarkers flag must be zero or one."),
                }).ToArray();
                if (!flags[0]) throw new NotSupportedException("Hiding every map marker has no source caller.");
                world.ShowAllMapMarkers(flags[1], flags[2]);
                return true;
            case "setactorfullname":
                if (arguments.Count != 1) throw new InvalidDataException("SetActorFullName requires one name form.");
                world.SetActorFullName(target, bindings.Form(arguments[0]).FormKey);
                return true;
            case "modpcmiscstat":
                var statistic = arguments.Where(argument => argument != ",").ToArray();
                if (parts.Length != 1 || statistic.Length != 2)
                    throw new InvalidDataException("ModPCMiscStat requires a statistic name and an integer amount.");
                var amount = number(statistic[1]);
                if (!double.IsFinite(amount) || amount != Math.Truncate(amount) || amount is < int.MinValue or > int.MaxValue)
                    throw new InvalidDataException("ModPCMiscStat amount must be an integer.");
                (host.Command ?? throw new NotSupportedException("ModPCMiscStat has no player statistic owner."))(source, bindings, operation,
                    ["\"" + text(statistic[0]) + "\"", ((int)amount).ToString(CultureInfo.InvariantCulture)]);
                return true;
            default:
                return false;
        }
    }

    // PipBoyRadio 1|0 or tune <station>. 1 resumes the last tuned station;
    // with no tuned station the receiver has nothing to resume.
    private void PipBoyRadio(IReadOnlyList<string> arguments, Func<string, double> number, Func<string, FalloutFormKey> reference)
    {
        var radio = world.PipBoyRadio;
        if (arguments[0].Equals("tune", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count != 2) throw new InvalidDataException("PipBoyRadio tune requires a station reference.");
            radio.Select(FalloutRadioStation.Read(records, records.GetEffective(reference(arguments[1]))));
            return;
        }
        if (arguments.Count != 1) throw new InvalidDataException("PipBoyRadio on/off takes no station.");
        var enable = number(arguments[0]) switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("PipBoyRadio state must be zero or one."),
        };
        if (!enable) radio.Off();
        else if (radio.CurrentStation is null && radio.LastStation is { } last)
            radio.Select(FalloutRadioStation.Read(records, records.GetEffective(last)));
    }
}
