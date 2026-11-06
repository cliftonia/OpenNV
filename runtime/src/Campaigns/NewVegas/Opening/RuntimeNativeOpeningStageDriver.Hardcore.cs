using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Content.Definitions;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Presentation.Ui;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

internal partial class RuntimeNativeOpeningStageDriver
{
    private FalloutHardcoreNeeds _needs = null!;

    // Need stages are selected from the stored need values, so loading a save
    // restores its diseases without notices. The load applies its stored
    // Hardcore flag through SetHardcore, clearing needs outside Hardcore.
    private void BindHardcoreNeeds()
    {
        _needs = new(_pluginStack);
        _playerSkills.NeedStageSpells = () => _vitals is { } vitals ? _needs.ConstantSpells(vitals.StoredNeed) : [];
        // The stored values select the stages, so this reset needs no notices.
        if (!_scripts.Session.Hardcore && _vitals.State.HasNeeds) SetHardcore(false);
        _vitals.NeedsChanged += ApplyNeedStages;
    }

    // Crippled limbs are the six limb conditions (not the brain) at zero.
    // There is nothing to show before the player's vitals exist.
    internal NativeHudHardcore? HudHardcore
    {
        get
        {
            if (_vitals is null) return null;
            var vitals = Vitals;
            var parts = PlayerBodyParts;
            var crippled = 0;
            for (var limb = (int)FalloutActorValueId.PerceptionCondition; limb <= (int)FalloutActorValueId.RightMobilityCondition; limb++)
                if (parts.LimbCondition(limb, vitals.MaximumHitPoints, vitals.LimbDamage) <= 0) crippled++;
            return new(_scripts.Session.Hardcore, vitals.Dehydration, vitals.Hunger, vitals.SleepDeprivation, vitals.RadiationRads, crippled);
        }
    }

    private void SetHardcore(bool enabled)
    {
        if (!enabled && _vitals.State.HasNeeds) _vitals.Publish(FalloutHardcoreNeeds.Reset(_vitals.State));
        _scripts.Session.Hardcore = enabled;
    }

    // Runs once per player update while the game runs in Hardcore. Disabling
    // the Pip-Boy control skips it without moving the anchors. Anchors reset
    // only on load or new game, so re-enabling Hardcore later in a session
    // catches up the time since the last Hardcore update (open spec question).
    private void AdvanceHardcoreNeeds()
    {
        if (!_scripts.Session.Hardcore || !PlayerControls.PipBoy || _vitals.State.ExactHitPoints <= 0) return;
        var clock = _gameTime ?? throw new NotSupportedException("Hardcore needs have no shared game clock.");
        var before = _vitals.State;
        var after = _needs.Accrue(before, clock.DaysPassed, clock.TimeScale, _scripts.Session.PlayerSleeping);
        if (!ReferenceEquals(before, after)) _vitals.Publish(after);
    }

    // Leaving a stage removes its disease and entering one casts the next
    // (both through the constant-effect selection); the player is told which.
    private void ApplyNeedStages(GameplayVitals before, GameplayVitals after)
    {
        var changes = _needs.Changes(before, after);
        if (changes.Count == 0) return;
        foreach (var change in changes)
        {
            if (change.Current?.Spell is { } spell && _needs.EntryHealthDamage(spell) is { } damage) _vitals.Damage(damage);
            _inventory.Notifications.Publish(FalloutHardcoreNeeds.Notices(change, _raceSexContract.Player));
            GD.Print($"OPENNV_HARDCORE_NEED_STAGE av={change.ActorValue} stage={change.Current?.Form.ToString() ?? "none"} " +
                $"previous={change.Previous?.Form.ToString() ?? "none"} value={after.Need(change.ActorValue):R}");
        }
        _playerAbilities.Synchronize();
    }
}
