namespace OpenNV.Runtime.Content;

internal sealed partial class FalloutTerminalMenu
{
    private bool _forceBack;

    // ForceTerminalBack inside a result fragment returns after the selection
    // completes, as if the player pressed back, and redraws source conditions.
    // Outside a fragment it acts on the active page immediately.
    internal void ForceBack()
    {
        if (!Active) throw new InvalidOperationException("ForceTerminalBack has no active terminal menu.");
        if (_executing)
        {
            _forceBack = true;
            return;
        }
        _ = Back();
        Refresh();
    }

    private void ApplyForcedBack()
    {
        if (!_forceBack) return;
        _forceBack = false;
        _ = Back();
        Refresh();
    }
}
