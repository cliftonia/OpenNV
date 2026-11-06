using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.World.Actors;

internal sealed partial class RuntimeNativeActorCombat
{
    // UseItemAt and UseWeapon keep the equipped item from wearing through use.
    private bool PackageSuppressesWeaponWear => _actor is RuntimeNativeNpc { SteppedPackageUsesItem: true };

    internal FalloutWanderProgress? PackageMoveRequest(FalloutFormKey package) =>
        _state.PackageMotion is { } motion && motion.Package == package ? motion.Wander : null;

    internal void SetPackageSteps(FalloutPluginRecord package, FalloutPackageStepProgress steps, bool clearMove = false)
    {
        if (_state.PackageMotion is not { } motion || motion.Package != package.FormKey)
            throw new InvalidOperationException("Stepped package has no matching native motion observation.");
        _state.PackageMotion = motion with { Steps = steps.Copy(), Wander = clearMove ? null : motion.Wander };
    }

    // Source coordinates are the actor's cell-parent Gamebryo frame.
    internal Vector3 PackageNativePoint(IReadOnlyList<float> source) =>
        _actor.GetParent<Node3D>().ToGlobal(new Vector3(source[0], source[2], -source[1]) * _skeleton.UnitsToMetres);

    internal float[] PackageSourcePoint(Vector3 global)
    {
        var local = _actor.GetParent<Node3D>().ToLocal(global) / _skeleton.UnitsToMetres;
        return [local.X, -local.Z, local.Y];
    }

    // The package weapon declared by PTDT: whether the actor owns a match,
    // its clip and whether it is melee. Category codes follow the PTDT enum.
    internal (bool Has, int Clip, bool Melee) PackageWeapon(FalloutPackageTarget? target)
    {
        var context = _context ?? throw new NotSupportedException("Package weapon has no gameplay context.");
        var owned = _world.Inventory(_state.Reference, context.Level(), context.Globals).Contents;
        var weapons = owned.Items.Where(item => item.RecordType == "WEAP" && item.Count > 0)
            .Select(item => _world.WeaponPresentation(item.FormKey, false, owned.WeaponModMask(item.FormKey)));
        var match = target switch
        {
            { Type: 1, Form: { } form } => weapons.FirstOrDefault(weapon => weapon.Form == form),
            { Type: 2, RawTarget: 22 } => weapons.FirstOrDefault(weapon => weapon.IsMeleeWeapon),
            { Type: 2, RawTarget: 23 } => weapons.FirstOrDefault(weapon => !weapon.IsMeleeWeapon),
            _ => weapons.FirstOrDefault(),
        };
        return match is null ? (false, 0, false) : (true, match.ClipSize, match.IsMeleeWeapon);
    }
}
