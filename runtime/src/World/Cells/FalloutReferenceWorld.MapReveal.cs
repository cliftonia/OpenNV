using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.World.Cells;

internal sealed partial class FalloutReferenceWorld
{
    private const byte HiddenFromShowAllMarkerFlag = 4;

    // ShowAllMapMarkers visible canTravel includeHidden. Source markers flagged
    // "show all hidden" stay untouched unless the third operand admits them.
    // Each reveal reuses ShowMap, so travel already earned is never revoked.
    internal void ShowAllMapMarkers(bool canTravel, bool includeHidden)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var marker in FalloutWorldMap.SourceMarkers(records))
            if (includeHidden || (marker.Marker.Flags & HiddenFromShowAllMarkerFlag) == 0)
                ShowMap(marker.Marker.Reference, canTravel);
    }
}
