namespace OpenNV.Runtime.Content;

// Neutral melee/unarmed VATS contract over the source melee GMSTs. A melee
// action targets the whole actor: the chance is capped by fVATSMaxChance and
// scaled by attack-arm condition; geometry, spread and visibility do not apply.
// Reach and approach distances are surface-to-surface metres, like the
// ordinary player melee selection they extend.
internal sealed class FalloutVatsMeleeChance(FalloutPluginStack records)
{
    internal float Calculate(float leftArmCondition, float rightArmCondition)
    {
        if (!float.IsFinite(leftArmCondition) || leftArmCondition is < 0 or > 1 ||
            !float.IsFinite(rightArmCondition) || rightArmCondition is < 0 or > 1)
            throw new InvalidDataException("VATS melee arm condition is invalid.");
        var arms = Setting("fVATSMeleeArmConditionBase") +
            Setting("fVATSMeleeArmConditionMult") * ((leftArmCondition + rightArmCondition) / 2);
        var chance = 100 * Setting("fVATSMeleeChanceMult") * Math.Min(arms, 1);
        return Math.Clamp(Math.Min(chance, Setting("fVATSMaxChance")), 0, 100);
    }

    // The ordinary reach is extended for target admission, never past the
    // absolute melee engage distance.
    internal float Range(float reach, float units)
    {
        RequireDistance(reach, units);
        return Math.Min(reach * Setting("fVATSMeleeReachMult"), Setting("fVATSMeleeMaxDistance") * units);
    }

    // A target beyond ordinary reach is approached until this surface distance.
    internal float ApproachDistance(float reach, bool handToHand, float units)
    {
        RequireDistance(reach, units);
        return reach * Setting(handToHand ? "fVATSH2HWarpDistanceMult" : "fVATSMeleeWarpDistanceMult");
    }

    internal float ActionPointCost(float cost)
    {
        if (!float.IsFinite(cost) || cost < 0) throw new InvalidDataException("VATS melee AP cost is invalid.");
        return cost * Setting("fActionPointsVATSMeleeMult");
    }

    private static void RequireDistance(float reach, float units)
    {
        if (!float.IsFinite(reach) || reach < 0 || !float.IsFinite(units) || units <= 0)
            throw new InvalidDataException("VATS melee reach is invalid.");
    }

    private float Setting(string name)
    {
        var value = FalloutGameSettingFloats.Read(records, name);
        return float.IsFinite(value) && value >= 0 ? value : throw new InvalidDataException("VATS setting is invalid: " + name);
    }
}
