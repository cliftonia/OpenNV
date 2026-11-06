using System.Xml.Linq;
using OpenNV.Runtime.Content;

namespace OpenNV.Runtime.Presentation.Ui;

// Player values behind the HUD HardcoreMode group. Rads and crippled limbs
// show in every mode; the three needs only in Hardcore.
internal sealed record NativeHudHardcore(bool Hardcore, float Dehydration, float Hunger, float SleepDeprivation,
    float RadiationRads, int CrippledLimbs);

internal readonly record struct NativeHudMeterThresholds(float HalfAlpha, float FullAlpha, float Red, float Flash)
{
    internal static NativeHudMeterThresholds Read(FalloutPluginStack records) => new(
        FalloutGameSettingFloats.Read(records, "fHCHalfAlphaTextThreshold"), FalloutGameSettingFloats.Read(records, "fHCFullAlphaTextThreshold"),
        FalloutGameSettingFloats.Read(records, "fHCRedTextThreshold"), FalloutGameSettingFloats.Read(records, "fHCFlashTextThreshold"));

    // Crippled limbs drive the LMB label just past each style threshold.
    internal float CrippledLimbs(int count) => count switch
    {
        <= 0 => 0,
        1 => HalfAlpha + 1,
        2 => FullAlpha + 1,
        3 => Red + 1,
        _ => Flash + 1,
    };

    // Warning colour and alpha for a label; flashing labels use the pulse.
    internal (bool Warning, float Alpha) Style(float value, float pulse) =>
        value >= Flash ? (true, pulse) : value >= Red ? (true, 255) : value >= FullAlpha ? (false, 255) :
        value >= HalfAlpha ? (false, 124) : (false, 0);
}

// Flashing labels pulse between 128 and 255 at half an alpha step per
// millisecond, reversing at either end.
internal struct NativeHudMeterPulse
{
    private float _alpha;
    private bool _falling;
    internal readonly float Alpha => _alpha == 0 ? 255 : _alpha;

    internal void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var alpha = Alpha + (float)(seconds * 500) * (_falling ? -1 : 1);
        while (alpha is > 255 or < 128)
        {
            if (alpha > 255) { alpha = 510 - alpha; _falling = true; }
            else { alpha = 256 - alpha; _falling = false; }
        }
        _alpha = alpha;
    }
}

internal partial class NativeOwnedGameplayHud
{
    private static readonly string[] HardcoreLabels = ["Dehydration", "Hunger", "Sleep", "Rads", "LMBs"];
    private Func<NativeHudHardcore?>? _hardcore;
    private XElement? _hardcoreBranch;
    private XElement[] _hardcoreTiles = [];
    private NativeHudMeterThresholds _thresholds;
    private NativeHudMeterPulse _pulse;
    private (bool, float)[] _lastHardcoreStyles = [];

    private void InitializeHardcore(FalloutPluginStack records, Func<string, XElement> branch, Func<NativeHudHardcore?>? hardcore)
    {
        if (hardcore is null || _hp is null) return;
        _hardcore = hardcore;
        _thresholds = NativeHudMeterThresholds.Read(records);
        _hardcoreBranch = branch("HardcoreMode");
        _tiles.Bind(_hardcoreBranch, "locus", 1);
        _hardcoreTiles = HardcoreLabels.Select(name => _hardcoreBranch.Elements("text").Single(tile => (string?)tile.Attribute("name") == name)).ToArray();
        _lastHardcoreStyles = new (bool, float)[_hardcoreTiles.Length];
        foreach (var tile in _hardcoreTiles) _tiles.Bind(tile, "alpha", 0);
        SetMeta("opennv_hud_hardcore_placement_unverified", true);
    }

    // The source group has no authored position; it sits above the HP branch
    // with its labels laid out left to right, 10 units apart.
    private void LayoutHardcore()
    {
        if (_hardcoreBranch is null) return;
        _tiles.Bind(_hardcoreBranch, "x", _tiles.Number(_hp!, "x"));
        _tiles.Bind(_hardcoreBranch, "y", _tiles.Number(_hp!, "y") - _tiles.Number(_hardcoreBranch, "height"));
        var right = 0f;
        foreach (var tile in _hardcoreTiles)
        {
            right += _tiles.Number(tile, "width");
            _tiles.Bind(tile, "x", right);
            right += 10;
        }
    }

    private void UpdateHardcore(double delta)
    {
        if (_hardcore is null || _hardcoreBranch is null) return;
        var values = _hardcore();
        // The pulse advances once per update, as the Hunger meter drives it.
        _pulse.Advance(delta);
        float[] shown = values is null ? [0, 0, 0, 0, 0] :
        [
            values.Hardcore ? values.Dehydration : 0, values.Hardcore ? values.Hunger : 0,
            values.Hardcore ? values.SleepDeprivation : 0, values.RadiationRads, _thresholds.CrippledLimbs(values.CrippledLimbs),
        ];
        var changed = false;
        for (var index = 0; index < _hardcoreTiles.Length; index++)
        {
            var style = _thresholds.Style(shown[index], _pulse.Alpha);
            if (style == _lastHardcoreStyles[index]) continue;
            _lastHardcoreStyles[index] = style;
            _hardcoreTiles[index].SetElementValue("systemcolor", style.Warning ? "entity_hudalt" : "entity_hudmain");
            _tiles.Bind(_hardcoreTiles[index], "alpha", style.Alpha);
            changed = true;
        }
        if (changed) QueueRedraw();
    }
}
