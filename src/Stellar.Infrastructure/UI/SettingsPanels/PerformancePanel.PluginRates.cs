using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Per-plugin update-rate controls for the Settings → Performance panel.
/// Each plugin row has: name label, a wide rate slider with a small knob (snap stops, 0 =
/// follow global) that fills the row, a one-line value readout (shows the live ramp rate in
/// accent while ramping), and a self-documenting "Self-rate" cycle button (Off → Boost →
/// Self-managed).
/// </summary>
internal sealed partial class PerformancePanel
{
    // Snap stops for the per-plugin rate cycle button. Index 0 = "Follow global" (stored as 0).
    private static readonly int[] PluginStops =
    {
        0, 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz,
    };

    private const int MaxPluginRows = 64;
    // Fixed column widths. The slider is pinned small; the middle gap is a Weight:1 CellElement that
    // absorbs all leftover row width, so the self-rate button right-aligns regardless of viewport width.
    private const float PluginNameWidth = 140f;
    private const float PluginSliderHandle = 7f;        // small knob (renderer default is 13)
    private const float PluginRateLabelWidth = 96f;     // value readout — fits "Follow global" on one line
    private const float PluginModeButtonWidth = 115f;

    // Refreshed each frame by the outer ConditionalElement's When predicate.
    private IReadOnlyList<PluginInfo> _pluginCache = Array.Empty<PluginInfo>();

    internal HudElement BuildPluginSection()
    {
        var slots = new HudElement[MaxPluginRows];
        for (var i = 0; i < MaxPluginRows; i++) slots[i] = BuildPluginRow(i);
        var list = new ListElement(() => _pluginCache.Count, slots);
        return new ConditionalElement(
            () => { _pluginCache = _inventory.List(); return _pluginCache.Count > 0; },
            new ScrollElement(list, Height: 220f),
            new TextElement(() => "No plugins loaded.", () => _theme.Colors.TextMuted));
    }

    // Drop the redundant "Stellar." prefix every plugin shares, so names fit the column and don't spill onto the slider.
    private static string ShortName(string? displayName)
    {
        var n = displayName ?? "";
        return n.StartsWith("Stellar.", StringComparison.Ordinal) ? n.Substring("Stellar.".Length) : n;
    }

    private HudElement BuildPluginRow(int idx)
    {
        PluginInfo? At() => idx < _pluginCache.Count ? _pluginCache[idx] : null;
        string Id() => At()?.Id ?? "";

        return new RowElement(new HudElement[]
        {
            new TextElement(() => ShortName(At()?.DisplayName),
                () => At()?.IsEnabled == true ? null : _theme.Colors.TextMuted,
                Width: PluginNameWidth, NoWrap: true),                                    // name (fixed; "Stellar." stripped)

            new SliderElement(() => GetPluginSliderIndex(Id()), v => SetPluginSliderIndex(Id(), v),
                0f, PluginStops.Length - 1, HandleSize: PluginSliderHandle),  // elastic track (fills the row) + small knob

            // Value readout (one line): the configured rate, or the live ramp rate (accent) while ramping.
            new TextElement(() => PluginRateOrRampLabel(Id()),
                () => IsRamping(Id()) ? _theme.Colors.Accent : (ColorRgba?)null,
                Width: PluginRateLabelWidth, NoWrap: true),

            new ButtonElement(() => SelfRateButtonLabel(Id()), () => CycleSelfRate(Id()),
                Width: PluginModeButtonWidth),                                            // self-rate: Off/Boost/Self-managed
        }, Gap: 10f);
    }

    // Value readout: the live ramp rate (while a Boost/Self-managed ramp is raising it) else the configured rate.
    private string PluginRateOrRampLabel(string id)
    {
        if (!IsRamping(id)) return PluginRateLabel(id);
        var eff = _effectiveRateFor(id);
        return eff >= PerfControls.MaxUpdateRateHz ? "→ every frame" : $"→ {eff} Hz";
    }

    // --- rate slider <-> stored Hz (snap to the nearest PluginStops entry; index 0 = follow global) ---
    private float GetPluginSliderIndex(string id)
    {
        var hz = _prefs.GetPluginRate(id);            // 0 = follow global
        if (hz <= 0) return 0f;
        var best = 1; var bestDist = int.MaxValue;
        for (var i = 1; i < PluginStops.Length; i++)
        {
            var d = Math.Abs(PluginStops[i] - hz);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private void SetPluginSliderIndex(string id, float v)
    {
        var i = (int)Math.Round(v);
        if (i < 0) i = 0; else if (i >= PluginStops.Length) i = PluginStops.Length - 1;
        _prefs.SetPluginRate(id, PluginStops[i]);
    }

    // --- Self-rate cycle button: one self-documenting control replacing the old Self/Hold toggles ---
    // The three modes map onto the two backing bools (Sustained requires Self-control):
    //   Off          = (selfControl=false, sustained=false)  — follows the global / per-plugin rate
    //   Boost        = (selfControl=true,  sustained=false)  — may ramp up, released after a 10 s safety cap
    //   Self-managed = (selfControl=true,  sustained=true)   — plugin fully controls + holds its rate, no cap

    private string SelfRateMode(string id)
    {
        if (!_prefs.GetPluginSelfControl(id)) return "Off";
        return _prefs.GetPluginSustained(id) ? "Self-managed" : "Boost";
    }

    // Bare mode word only ("Off"/"Boost"/"Self-managed") — the section description carries the
    // "Self-rate" framing, so a prefix here is redundant and only widens the button. The longest
    // label "Self-managed" fits PluginModeButtonWidth (115px) with padding; verified in the sandbox.
    private string SelfRateButtonLabel(string id) => SelfRateMode(id);

    private void CycleSelfRate(string id)
    {
        switch (SelfRateMode(id))
        {
            case "Off":   _prefs.SetPluginSelfControl(id, true);  _prefs.SetPluginSustained(id, false); break; // -> Boost
            case "Boost": _prefs.SetPluginSustained(id, true);                                          break; // -> Self-managed
            default:      _prefs.SetPluginSelfControl(id, false); _prefs.SetPluginSustained(id, false); break; // Self-managed -> Off
        }
    }

    private string PluginRateLabel(string id)
    {
        var hz = _prefs.GetPluginRate(id);
        if (hz <= 0) return "Follow global";
        if (hz >= PerfControls.MaxUpdateRateHz) return "Every frame";
        return $"{hz} Hz";
    }

    private bool IsRamping(string id)
    {
        if (!_prefs.GetPluginSelfControl(id)) return false;
        var baseHz = _prefs.GetPluginRate(id) > 0 ? _prefs.GetPluginRate(id) : PerfControls.UpdateRateHz;
        return _effectiveRateFor(id) > baseHz;
    }
}
