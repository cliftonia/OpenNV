using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal enum FalloutScriptQualifierKind { Null, Reference, NonReference }

internal sealed partial class FalloutReferenceWorld
{
    // One "last resolved form" per script form and reference variable, shared
    // by every object running that script form. A zero value leaves it as is;
    // an unknown ID clears it. Session state, as in the engine; never saved.
    private readonly Dictionary<(FalloutFormKey Script, string Variable), FalloutFormKey?> _qualifierForms = [];
    private readonly HashSet<FalloutFormKey> _disabledScriptForms = [];

    internal (FalloutScriptQualifierKind Kind, FalloutFormKey Form) ResolveQualifier(FalloutFormKey script, string variable, double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > uint.MaxValue || value != Math.Truncate(value))
            throw new InvalidDataException("Script reference variable has no valid form identity.");
        var slot = (script, variable.ToLowerInvariant());
        if (value != 0)
        {
            var key = records.RuntimeFormKey((uint)value);
            _qualifierForms[slot] = (uint)value == 0x14 || records.TryGetEffective(key, out _) ? key : null;
        }
        if (_qualifierForms.GetValueOrDefault(slot) is not { } form) return (FalloutScriptQualifierKind.Null, default);
        return form == _enginePlayer || records.GetEffective(form).Signature is "REFR" or "ACHR" or "ACRE"
            ? (FalloutScriptQualifierKind.Reference, form) : (FalloutScriptQualifierKind.NonReference, form);
    }

    internal bool IsScriptFormDisabled(FalloutFormKey script) => _disabledScriptForms.Contains(script);

    internal void DisableScriptForm(FalloutFormKey script) => _disabledScriptForms.Add(script);
}
