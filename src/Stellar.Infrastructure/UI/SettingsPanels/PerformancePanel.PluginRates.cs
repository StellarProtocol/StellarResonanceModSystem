using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Per-plugin update-rate controls for the Settings → Performance panel.
/// Each plugin row has: name label, a small fixed-width rate slider (snap stops, 0 = follow
/// global), a value readout, a flexible gap that shows the live ramp indicator while ramping
/// and absorbs the row slack, and a right-aligned self-documenting "Self-rate" cycle button
/// (Off → Boost → Self-managed). The slider is pinned to a fixed width so it stays small and
/// renders consistently in-game.
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
    private const float PluginSliderWidth = 90f;        // small, fixed — the user wants a compact slider
    private const float PluginRateLabelWidth = 80f;     // value readout next to the slider
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

    private HudElement BuildPluginRow(int idx)
    {
        PluginInfo? At() => idx < _pluginCache.Count ? _pluginCache[idx] : null;
        string Id() => At()?.Id ?? "";

        return new RowElement(new HudElement[]
        {
            new TextElement(() => At()?.DisplayName ?? "",
                () => At()?.IsEnabled == true ? null : _theme.Colors.TextMuted,
                Width: PluginNameWidth, NoWrap: true),                                    // name (fixed)

            new SliderElement(() => GetPluginSliderIndex(Id()), v => SetPluginSliderIndex(Id(), v),
                0f, PluginStops.Length - 1, Width: PluginSliderWidth),                    // small fixed-width rate slider

            new TextElement(() => PluginRateLabel(Id()), Width: PluginRateLabelWidth),    // value readout

            // Flexible gap that absorbs the slack so the self-rate button right-aligns; shows the live
            // ramp indicator (accent) ONLY while ramping, empty otherwise.
            new CellElement(
                new TextElement(() => IsRamping(Id())
                        ? (_effectiveRateFor(Id()) >= PerfControls.MaxUpdateRateHz
                            ? "→ every frame" : $"→ {_effectiveRateFor(Id())} Hz")
                        : "",
                    () => _theme.Colors.Accent, Align: TextAlign.Center),
                Weight: 1f),

            new ButtonElement(() => SelfRateButtonLabel(Id()), () => CycleSelfRate(Id()),
                Width: PluginModeButtonWidth),                                            // self-rate: Off/Boost/Self-managed
        }, Gap: 10f);
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
