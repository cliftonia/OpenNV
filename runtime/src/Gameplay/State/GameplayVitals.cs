namespace OpenNV.Runtime.Gameplay.State;

internal sealed record GameplayVitals(
    int Level,
    int HitPoints,
    int MaximumHitPoints,
    int ActionPoints,
    int MaximumActionPoints,
    int ExperiencePoints,
    int NextLevelExperiencePoints, float HitPointFraction = 0,
    IReadOnlyDictionary<byte, float>? LimbDamage = null, float RadiationRads = 0, float ActionPointFraction = 0,
    float Dehydration = 0, float Hunger = 0, float SleepDeprivation = 0)
{
    // Hardcore needs share the radiation rule: additive damage, never below zero.
    internal float Need(int actorValue) => actorValue switch
    {
        73 => Dehydration,
        74 => Hunger,
        75 => SleepDeprivation,
        _ => throw new ArgumentOutOfRangeException(nameof(actorValue)),
    };
    internal GameplayVitals WithNeed(int actorValue, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Hardcore need value is not finite.");
        value = Math.Max(0, value);
        return actorValue switch
        {
            73 => this with { Dehydration = value },
            74 => this with { Hunger = value },
            75 => this with { SleepDeprivation = value },
            _ => throw new ArgumentOutOfRangeException(nameof(actorValue)),
        };
    }
    internal bool HasNeeds => Dehydration != 0 || Hunger != 0 || SleepDeprivation != 0;

    internal float ExactHitPoints => HitPoints - HitPointFraction;
    internal float ExactActionPoints => ActionPoints - ActionPointFraction;

    // A derived maximum is not a healing command. Preserve the authoritative
    // damage/AP deficits and the independent injury/radiation owners.
    internal GameplayVitals WithDerivedMaximums(GameplayVitals derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        var health = ExactHitPoints == 0 ? 0 :
            Math.Clamp(derived.MaximumHitPoints - (MaximumHitPoints - ExactHitPoints), 0, derived.MaximumHitPoints);
        var displayed = checked((int)MathF.Ceiling(health));
        var actionPoints = Math.Clamp(derived.MaximumActionPoints - (MaximumActionPoints - ExactActionPoints), 0, derived.MaximumActionPoints);
        var displayedActionPoints = checked((int)MathF.Ceiling(actionPoints));
        var result = derived with
        {
            HitPoints = displayed,
            HitPointFraction = displayed - health,
            ActionPoints = displayedActionPoints,
            ActionPointFraction = displayedActionPoints - actionPoints,
            LimbDamage = LimbDamage is null ? null : new Dictionary<byte, float>(LimbDamage),
            RadiationRads = RadiationRads,
            Dehydration = Dehydration, Hunger = Hunger, SleepDeprivation = SleepDeprivation,
        };
        result.Validate();
        return result;
    }
    internal GameplayVitals Damage(float amount)
    {
        if (!float.IsFinite(amount) || amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var remaining = Math.Max(0, ExactHitPoints - amount);
        var displayed = (int)MathF.Ceiling(remaining);
        return this with { HitPoints = displayed, HitPointFraction = displayed - remaining };
    }
    internal GameplayVitals Damage(float amount, byte part, float limbMultiplier)
    {
        if (part > 14 || !float.IsFinite(limbMultiplier) || limbMultiplier < 0)
            throw new ArgumentOutOfRangeException(nameof(part));
        var state = Damage(amount);
        var limbs = new Dictionary<byte, float>(LimbDamage ?? new Dictionary<byte, float>());
        limbs[part] = limbs.GetValueOrDefault(part) + amount * limbMultiplier;
        if (!float.IsFinite(limbs[part])) throw new InvalidDataException("Player limb damage exceeds finite storage.");
        return state with { LimbDamage = limbs };
    }
    internal static GameplayVitals Derive(double baseHealth, int level, double endurance, double agility,
        double healthEndurance, double healthLevel, double apBase, double apAgility,
        int experience, double xpBase, double xpBump)
    {
        static int Exact(double value)
        {
            if (!double.IsFinite(value) || value != Math.Truncate(value) || value <= 0 || value > int.MaxValue)
                throw new InvalidDataException("Owned vitals derivation requires a positive integral result.");
            return (int)value;
        }
        var hp = Exact(baseHealth + endurance * healthEndurance + (level - 1) * healthLevel);
        var ap = Exact(apBase + agility * apAgility);
        var next = Exact(level * ((level - 1) * xpBump / 2 + xpBase));
        var state = new GameplayVitals(level, hp, hp, ap, ap, experience, next);
        state.Validate();
        return state;
    }

    internal void Validate()
    {
        if (Level <= 0 || MaximumHitPoints <= 0 || HitPoints < 0 ||
            HitPoints > MaximumHitPoints || MaximumActionPoints <= 0 ||
            ActionPoints < 0 || ActionPoints > MaximumActionPoints ||
            !float.IsFinite(ActionPointFraction) || ActionPointFraction is < 0 or >= 1 || ExactActionPoints < 0 ||
            ExperiencePoints < 0 || NextLevelExperiencePoints <= 0 ||
            !float.IsFinite(HitPointFraction) || HitPointFraction is < 0 or >= 1 || ExactHitPoints < 0 ||
            !float.IsFinite(RadiationRads) || RadiationRads < 0 ||
            !ValidNeed(Dehydration) || !ValidNeed(Hunger) || !ValidNeed(SleepDeprivation) ||
            LimbDamage is { } limbs && limbs.Any(pair => pair.Key > 14 || !float.IsFinite(pair.Value) || pair.Value < 0))
            throw new InvalidOperationException("Saved gameplay vitals are invalid.");
        static bool ValidNeed(float value) => float.IsFinite(value) && value >= 0;
    }
}
