using System.Buffers.Binary;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Content;

internal enum FalloutPackageStep { Acquire, Travel, Wander, AmbushWait, FleeNotCombat, UseItemAt, UseWeapon, Done }

// PKW3: how a UseWeapon package fires. Flag bytes are independent booleans.
internal sealed record FalloutWeaponUse(bool AlwaysHit, bool DoNoDamage, bool CrouchToReload, bool HoldFireWhenBlocked,
    bool VolleyFire, bool RepeatFire, int Bursts, int VolleyMinimum, int VolleyMaximum, float PauseMinimum, float PauseMaximum)
{
    internal static FalloutWeaponUse Read(ReadOnlySpan<byte> data)
    {
        if (data.Length != 24) throw new InvalidDataException("UseWeapon PKW3 must be 24 bytes.");
        var pauseMinimum = BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
        var pauseMaximum = BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
        if (!float.IsFinite(pauseMinimum) || !float.IsFinite(pauseMaximum))
            throw new InvalidDataException("UseWeapon volley pause is not finite.");
        return new(data[0] != 0, data[1] != 0, data[2] != 0, data[3] != 0, data[4] == 1, data[5] == 1,
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..]), Math.Max(0, pauseMinimum), Math.Max(0, pauseMaximum));
    }
}

// Wander, UseItemAt, Ambush, FleeNotCombat and UseWeapon run as fixed step
// lists. A package lacking the target or location its type needs, and the
// unused CastMagic type, run as Travel (usually "stay here").
internal sealed record FalloutSteppedPackage(FalloutFormKey Form, string EditorId, byte Procedure, uint Flags, ushort SpecificFlags,
    FalloutPackageLocation? Location, FalloutPackageLocation? Zone, FalloutPackageTarget? Target, FalloutPackageTarget? AttackTarget,
    FalloutWeaponUse? Weapon, int DurationHours, IReadOnlyList<FalloutPackageStep> Steps)
{
    internal bool Running => (Flags & FalloutPackageFlags.AlwaysRun) != 0;
    internal bool Sneaking => (Flags & FalloutPackageFlags.AlwaysSneak) != 0;
    internal bool WeaponDrawn => (Flags & FalloutPackageFlags.WeaponDrawn) != 0;
    internal bool MustComplete => (Flags & FalloutPackageFlags.MustComplete) != 0;
    internal bool MustReach => (Flags & FalloutPackageFlags.MustReachLocation) != 0;
    internal bool OncePerDay => (Flags & FalloutPackageFlags.OncePerDay) != 0;
    internal bool ContinueDuringCombat => (Flags & FalloutPackageFlags.ContinueDuringCombat) != 0;
    internal bool HideWhileAmbushing => Procedure == 9 && (SpecificFlags & 1) != 0;
    internal bool SitDown => Procedure == 8 && (SpecificFlags & 2) != 0;

    internal static FalloutSteppedPackage Read(FalloutPluginRecord record)
    {
        var data = FalloutPackageData.Read(record);
        if (data.Procedure is not (5 or 8 or 9 or 10 or 11 or 16)) throw new InvalidDataException($"PACK {record.FormKey} is not a stepped package type.");
        var declaration = FalloutScriptPackage.Read(record);
        var fields = record.ReadSubrecords().ToArray();
        // PLD2 may be repeated (Ambush copies it after its PKAM marker); the
        // copies must agree, then one declaration is the zone.
        var zones = fields.Where(field => field.Signature == "PLD2").ToArray();
        if (zones.Skip(1).Any(field => !field.Data.Span.SequenceEqual(zones[0].Data.Span)))
            throw new InvalidDataException($"PACK {record.FormKey} repeats a conflicting second location.");
        var zone = zones.Length == 0 ? null : FalloutPackageLocation.Decode(record, zones[0].Data.Span);
        var target = fields.Any(field => field.Signature == "PTDT") ? FalloutPackageTarget.Read(record) : null;
        var attack = fields.Any(field => field.Signature == "PTD2") ? FalloutPackageTarget.Read(record, "PTD2") : null;
        var pkw3 = fields.Where(field => field.Signature == "PKW3").ToArray();
        if (pkw3.Length > 1) throw new InvalidDataException($"PACK {record.FormKey} repeats PKW3.");
        var weapon = pkw3.Length == 1 ? FalloutWeaponUse.Read(pkw3[0].Data.Span) : null;
        // Movement, weapon presentation and the shared lifecycle flags are owned;
        // the remaining general flags are reported once by the admission owner.
        const uint owned = FalloutPackageFlags.AlwaysRun | FalloutPackageFlags.AlwaysSneak | FalloutPackageFlags.WeaponDrawn |
            FalloutPackageFlags.ContinueDuringCombat;
        var flags = FalloutPackageFlags.Admit(record, data, owned);
        var specific = FalloutPackageFlags.AdmitSpecific(record, data, data.Procedure switch { 8 => 2, 9 => 1, _ => 0 });
        var location = declaration.LocationDeclaration;
        IReadOnlyList<FalloutPackageStep> steps = data.Procedure switch
        {
            5 when location is not null => [FalloutPackageStep.Travel, FalloutPackageStep.Wander, FalloutPackageStep.Done],
            8 when target is not null => [FalloutPackageStep.Acquire, FalloutPackageStep.Travel, FalloutPackageStep.UseItemAt, FalloutPackageStep.Done],
            9 when location is not null => [FalloutPackageStep.Travel, FalloutPackageStep.AmbushWait, FalloutPackageStep.Done],
            10 => [FalloutPackageStep.FleeNotCombat, FalloutPackageStep.Done],
            16 when target is not null && weapon is not null => [FalloutPackageStep.Acquire, FalloutPackageStep.Travel, FalloutPackageStep.UseWeapon, FalloutPackageStep.Done],
            _ => [FalloutPackageStep.Travel, FalloutPackageStep.Done],
        };
        if (steps.Count == 2 && steps[0] == FalloutPackageStep.Travel)
            FalloutScriptCommandDeferral.WarnPackage($"invalid-package:{record.FormKey}",
                $"Package '{declaration.EditorId}' is not a valid package because it is missing necessary Target Or Location Info; it runs as Travel.");
        return new(record.FormKey, declaration.EditorId, data.Procedure, flags, specific, location, zone, target, attack, weapon,
            FalloutPackageSchedule.Read(record).Duration, steps);
    }
}
