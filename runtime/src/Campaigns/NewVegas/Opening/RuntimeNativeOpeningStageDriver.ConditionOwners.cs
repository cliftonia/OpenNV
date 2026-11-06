using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;
using OpenNV.Runtime.SceneGraph;
using OpenNV.Runtime.World.Actors;
using OpenNV.Runtime.World.Cells;

namespace OpenNV.Runtime.Campaigns.NewVegas.Opening;

// Live scene and player-effect owners queried by shared conditions.
internal partial class RuntimeNativeOpeningStageDriver
{
    private const int MaximumProbeHits = 32;

    private void BindConditionOwners()
    {
        var references = _scripts.References!;
        references.BindPlayerActiveEffects(PlayerActiveEffects);
        references.VatsConditions.BindCameraSpace(new(CameraProbePose, CameraProbeBoundCenter, CameraProbeCast));
    }

    // Constant abilities, perk spells and worn enchantments, timed ingestible
    // effects and delivered actor effects, in every state.
    private IReadOnlyList<FalloutActiveEffect> PlayerActiveEffects()
    {
        var player = _pluginStack.RuntimeFormKey(0x14);
        return _playerSkills.SelectedConstantEffects().SelectMany(source => FalloutActiveEffect.Declared(_pluginStack, source))
            .Concat(_ingestibles.ActiveEffects).Concat(_deliveredEffects.ActiveOn(player)).ToArray();
    }

    // The live placement of a resident reference or the player, in game units.
    private FalloutReferencePlacement? LivePlacement(FalloutFormKey reference)
    {
        var node = LiveNode(reference);
        if (node is null) return null;
        var position = node.GlobalPosition / _player.UnitsToMeters;
        var rotation = GamebryoCoordinate.ReferenceEuler(node.GlobalBasis);
        var cell = reference == _pluginStack.RuntimeFormKey(0x14) ? _activeCell : _scripts.References!.Placement(reference).Cell;
        return new(cell, [position.X, -position.Z, position.Y], [rotation.X, rotation.Y, rotation.Z]);
    }

    private Node3D? LiveNode(FalloutFormKey reference) => reference == _pluginStack.RuntimeFormKey(0x14) ? _player :
        ReferencePresentation().Nodes.GetValueOrDefault(reference);

    private (System.Numerics.Vector3 Position, float Heading) CameraProbePose(FalloutFormKey subject)
    {
        var placement = LivePlacement(subject) ?? throw new NotSupportedException($"VATS camera subject {subject} has no live 3D.");
        return (new(placement.Position[0], placement.Position[1], placement.Position[2]), placement.RotationRadians[2]);
    }

    private System.Numerics.Vector3? CameraProbeBoundCenter(FalloutFormKey target)
    {
        if (LiveNode(target) is not { } node || !NodeTraversal.Descendants<MeshInstance3D>(node).Any()) return null;
        return GameUnits(ActorModelSlice.WorldBounds(node).GetCenter());
    }

    private System.Numerics.Vector3 GameUnits(Vector3 point)
    {
        var scaled = point / _player.UnitsToMeters;
        return new(scaled.X, -scaled.Z, scaled.Y);
    }

    private Vector3 SceneMetres(System.Numerics.Vector3 point) =>
        new Vector3(point.X, point.Z, -point.Y) * _player.UnitsToMeters;

    // Every body the segment strikes in the player's physics world, mapped to
    // its owning placed reference.
    private IReadOnlyList<FalloutCameraProbeHit> CameraProbeCast(System.Numerics.Vector3 start, System.Numerics.Vector3 end)
    {
        var from = SceneMetres(start);
        var to = SceneMetres(end);
        var length = from.DistanceTo(to);
        if (length <= 0) return [];
        var owners = ReferencePresentation().Nodes.ToDictionary(pair => pair.Value.GetInstanceId(), pair => pair.Key);
        var space = _player.GetWorld3D().DirectSpaceState;
        var exclusions = new Godot.Collections.Array<Rid>();
        var hits = new List<FalloutCameraProbeHit>();
        while (hits.Count < MaximumProbeHits)
        {
            using var query = PhysicsRayQueryParameters3D.Create(from, to, uint.MaxValue, exclusions);
            using var hit = space.IntersectRay(query);
            if (hit.Count == 0) break;
            var collider = hit["collider"].AsGodotObject() as Node;
            var reference = ProbeOwner(collider, owners);
            hits.Add(new(reference, from.DistanceTo(hit["position"].AsVector3()) / length,
                reference is { } owner && (owner == _pluginStack.RuntimeFormKey(0x14) ||
                    _pluginStack.GetEffective(owner).Signature is "ACHR" or "ACRE")));
            exclusions.Add(hit["rid"].AsRid());
        }
        return hits;
    }

    private FalloutFormKey? ProbeOwner(Node? collider, IReadOnlyDictionary<ulong, FalloutFormKey> owners)
    {
        for (var node = collider; node is not null; node = node.GetParent())
        {
            if (node == _player) return _pluginStack.RuntimeFormKey(0x14);
            if (owners.TryGetValue(node.GetInstanceId(), out var reference)) return reference;
        }
        return null;
    }
}
