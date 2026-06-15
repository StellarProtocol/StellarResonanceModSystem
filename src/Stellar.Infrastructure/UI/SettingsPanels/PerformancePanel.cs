using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Performance panel. Two controls, wired to <see cref="PerfPrefs"/>:
/// the <b>Stellar Update Rate</b> slider (how many times/sec the framework crosses
/// into the managed runtime to drive its HUD/input/overlays — the dominant lever on
/// Stellar's game-FPS cost) and the <b>Uncap Frame Rate</b> toggle (removes the game's
/// V-Sync/FPS cap). Wording is deliberately <em>relative</em> (no fake precision): the
/// slider snaps to meaningful stops and the description re-labels by band.
/// </summary>
internal sealed class PerformancePanel
{
    // Meaningful slider stops. Index 0..N-1 map to these Hz; the top is "every frame".
    private static readonly int[] RateStops = { 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz };

    private readonly PerfPrefs _prefs;
    private readonly ITheme _theme;

    public PerformancePanel(PerfPrefs prefs, ITheme theme)
    {
        _prefs = prefs;
        _theme = theme;
    }

    public HudElement Describe() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "Stellar Update Rate", Emphasis: true),
        new TextElement(() =>
            "How often Stellar refreshes its HUD, input and overlays each second. Lower = more game FPS; higher = smoother Stellar UI.",
            () => _theme.Colors.TextMuted),
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
            new TextElement(() => "Uncap Frame Rate"),
        }, Gap: 6f),
        new TextElement(() =>
            "Removes the game's frame-rate cap (disables V-Sync + the FPS limit) so it runs as fast as your GPU allows. " +
            "Higher FPS, but more GPU heat/power/fan noise and possible screen tearing.",
            () => _theme.Colors.TextMuted),
    });

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
        return hz >= PerfControls.MaxUpdateRateHz ? "Every frame" : $"{hz} Hz";
    }

    private string RateDescription()
    {
        var hz = _prefs.UpdateRateHz;
        if (hz <= 15)
            return "Best game FPS — Stellar barely touches your frame rate. The HUD, cooldowns and on-screen info refresh " +
                   "only ~10-15×/sec, so they visibly lag and typing/dragging in Stellar feels delayed.";
        if (hz >= PerfControls.MaxUpdateRateHz)
            return "Smoothest, most responsive Stellar UI — updates every rendered frame. Costs the most game FPS.";
        if (hz <= PerfControls.DefaultUpdateRateHz)
            return "Near-vanilla game FPS while keeping the HUD and input smooth enough that most players won't notice. The sweet spot.";
        return "Smoother Stellar UI than the default, at some game-FPS cost — higher is smoother but more costly.";
    }
}
