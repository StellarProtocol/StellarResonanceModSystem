using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Per-plugin update-rate controls for the Settings → Performance panel.
/// Each plugin row has: name label, a fixed-width rate-slider cell (so the columns
/// stay deterministic and never overlap), a rate/ramp value label (folded), and a
/// single self-documenting "Self-rate" cycle button (Off → Boost → Self-managed).
/// </summary>
internal sealed partial class PerformancePanel
{
    // Snap stops for the per-plugin slider. Index 0 = "Follow global" (stored as 0).
    private static readonly int[] PluginStops =
    {
        0, 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz,
    };

    private const int MaxPluginRows = 64;
    // Fixed column widths. Tuned via the UI sandbox so the row total + 3 gaps fits the
    // ~577px scroll viewport (586 content − ~9 scrollbar) with NO overlap and a usable slider.
    private const float PluginNameWidth = 120f;
    private const float PluginSliderWidth = 110f;
    private const float PluginRateLabelWidth = 95f;
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
                Width: PluginNameWidth, NoWrap: true),
            // The slider lives in a STABLE fixed-width cell (not the elastic flex cell) so every
            // column is a known width and the row total is deterministic — this is the fix for the
            // slider being squeezed into / overlapping the adjacent value + button columns.
            new CellElement(
                new SliderElement(() => GetPluginSliderIndex(Id()),
                    v => SetPluginSliderIndex(Id(), v), 0f, PluginStops.Length - 1),
                Width: PluginSliderWidth),
            new TextElement(() => PluginRateOrRampLabel(Id()),
                () => IsRamping(Id()) ? _theme.Colors.Accent : (ColorRgba?)null,
                Width: PluginRateLabelWidth),
            new ButtonElement(() => SelfRateButtonLabel(Id()),
                () => CycleSelfRate(Id()), Width: PluginModeButtonWidth),
        }, Gap: 10f);
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

    private int GetPluginSliderIndex(string id)
    {
        var hz = _prefs.GetPluginRate(id);
        if (hz <= 0) return 0;
        var best = 1;
        var bestDist = Math.Abs(PluginStops[1] - hz);
        for (var i = 2; i < PluginStops.Length; i++)
        {
            var d = Math.Abs(PluginStops[i] - hz);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private void SetPluginSliderIndex(string id, float v)
    {
        var idx = Math.Clamp((int)Math.Round(v), 0, PluginStops.Length - 1);
        _prefs.SetPluginRate(id, PluginStops[idx]);
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

    private string PluginRateOrRampLabel(string id)
    {
        if (IsRamping(id))
        {
            var eff = _effectiveRateFor(id);
            return eff >= PerfControls.MaxUpdateRateHz ? "Every frame ↑" : $"{eff} Hz ↑";
        }
        return PluginRateLabel(id);
    }
}
