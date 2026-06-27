using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Per-plugin update-rate controls for the Settings → Performance panel.
/// Each plugin row has: name label, rate slider (snapped to meaningful stops),
/// rate/ramp value label (folded), Self-rate toggle, Self caption, Sustained toggle,
/// and a Hold caption (dimmed when Self is off).
/// </summary>
internal sealed partial class PerformancePanel
{
    // Snap stops for the per-plugin slider. Index 0 = "Follow global" (stored as 0).
    private static readonly int[] PluginStops =
    {
        0, 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz,
    };

    private const int MaxPluginRows = 64;
    private const float PluginNameWidth = 150f;
    private const float PluginRateLabelWidth = 96f;

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
            new SliderElement(() => GetPluginSliderIndex(Id()),
                v => SetPluginSliderIndex(Id(), v), 0f, PluginStops.Length - 1),
            new TextElement(() => PluginRateOrRampLabel(Id()),
                () => IsRamping(Id()) ? _theme.Colors.Accent : (ColorRgba?)null,
                Width: PluginRateLabelWidth),
            new ToggleElement(() => "",
                () => _prefs.GetPluginSelfControl(Id()),
                v => _prefs.SetPluginSelfControl(Id(), v)),
            new TextElement(() => "Self"),
            new ToggleElement(() => "",
                () => _prefs.GetPluginSustained(Id()),
                v => _prefs.SetPluginSustained(Id(), v),
                Enabled: () => _prefs.GetPluginSelfControl(Id())),
            new TextElement(() => "Hold",
                () => _prefs.GetPluginSelfControl(Id()) ? (ColorRgba?)null : _theme.Colors.TextMuted),
        }, Gap: 8f);
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
