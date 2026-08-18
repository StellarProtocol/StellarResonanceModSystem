using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Performance panel. Global rate + frame-cap controls, wired to
/// <see cref="PerfPrefs"/>. A per-plugin override section (implemented in the
/// <c>PerformancePanel.PluginRates.cs</c> partial) lets users assign individual
/// update rates or enable self-rate ramps per plugin.
/// </summary>
internal sealed partial class PerformancePanel
{
    // Meaningful slider stops for the global rate. Index 0..N-1 map to these Hz.
    private static readonly int[] RateStops = { 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz };

    private readonly PerfPrefs _prefs;
    private readonly ITheme _theme;
    private readonly IPluginInventory _inventory;
    private readonly Func<string, int> _effectiveRateFor;
    private readonly ILocalization _loc;

    public PerformancePanel(PerfPrefs prefs, ITheme theme, IPluginInventory inventory, Func<string, int> effectiveRateFor, ILocalization loc)
    {
        _prefs = prefs;
        _theme = theme;
        _inventory = inventory;
        _effectiveRateFor = effectiveRateFor;
        _loc = loc;
    }

    public HudElement Describe()
    {
        var pluginSection = BuildPluginSection();
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T("perf.updateRate"), Emphasis: true),
            new TextElement(() => _loc.T("perf.updateRate.desc"), () => _theme.Colors.TextMuted),
            new RowElement(new HudElement[]
            {
                new SliderElement(RateToSlider, SliderToRate, 0f, RateStops.Length - 1),
                new TextElement(RateValueLabel, Width: 96f),
            }, Gap: 8f),
            new TextElement(RateDescription, () => _theme.Colors.TextMuted),

            new SeparatorElement(),

            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", () => _prefs.Uncap, v => _prefs.Uncap = v),
                new TextElement(() => _loc.T("perf.uncap")),
            }, Gap: 6f),
            new TextElement(() => _loc.T("perf.uncap.desc"), () => _theme.Colors.TextMuted),

            new SeparatorElement(),
            new TextElement(() => _loc.T("perf.perPlugin"), Emphasis: true),
            new TextElement(() => _loc.T("perf.perPlugin.desc"), () => _theme.Colors.TextMuted),
            pluginSection,
        });
    }

    // --- slider <-> rate mapping (snap to the nearest meaningful stop) ---

    private float RateToSlider()
    {
        var hz = _prefs.UpdateRateHz;
        var best = 0;
        var bestDist = int.MaxValue;
        for (var i = 0; i < RateStops.Length; i++)
        {
            var d = Math.Abs(RateStops[i] - hz);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private void SliderToRate(float v)
    {
        var i = (int)Math.Round(v);
        if (i < 0) i = 0;
        else if (i >= RateStops.Length) i = RateStops.Length - 1;
        _prefs.UpdateRateHz = RateStops[i];
    }

    private string RateValueLabel()
    {
        var hz = _prefs.UpdateRateHz;
        return hz >= PerfControls.MaxUpdateRateHz ? _loc.T("perf.everyFrame") : _loc.TFormat("perf.hz", hz);
    }

    private string RateDescription()
    {
        var hz = _prefs.UpdateRateHz;
        if (hz <= 15) return _loc.T("perf.rateDesc.low");
        if (hz >= PerfControls.MaxUpdateRateHz) return _loc.T("perf.rateDesc.max");
        if (hz <= PerfControls.DefaultUpdateRateHz) return _loc.T("perf.rateDesc.default");
        return _loc.T("perf.rateDesc.high");
    }
}
