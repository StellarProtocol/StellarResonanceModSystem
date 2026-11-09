using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Performance: a top row of two panels — "Stellar update rate" (stepped slider over <see cref="RateStops"/>,
/// value, tick labels, a description per range) and "Frame rate" (uncap switch + a status / caution line) — over the
/// full-width "Per-plugin rate" list (the <c>PerformancePanel.PluginRates.cs</c> partial). Wired to
/// <see cref="PerfPrefs"/>.
/// </summary>
internal sealed partial class PerformancePanel
{
    // Meaningful slider stops for the global rate. Index 0..N-1 map to these Hz.
    private static readonly int[] RateStops = { 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz };
    private const float ValueWidth = 86f;
    private const float TickFontSize = 11f;
    private const float RangeDescHeight = 51f;   // three 14 px lines: the longest range description, so nothing moves

    private readonly PerfPrefs _prefs;
    private readonly IPluginInventory _inventory;
    private readonly ILocalization _loc;
    private SettingsKit _k = null!;

    /// <summary>Display-only plugin names (the plugin's own localized launcher-tile title). Set by the wiring as an
    /// init property, not a ctor arg (keeps the constructor under the dependency cap). Null → internal names.
    /// Read only from row Funcs (render-driven), never from a per-tick path.</summary>
    internal IPluginDisplayNames? DisplayNames { get; init; }

    // theme and effectiveRateFor are no longer read (the house kit carries the live colours; the ramp read-out is
    // gone with the per-plugin slider) — the parameters stay so the wiring is unchanged.
    public PerformancePanel(PerfPrefs prefs, ITheme theme, IPluginInventory inventory, Func<string, int> effectiveRateFor, ILocalization loc)
    {
        _prefs = prefs;
        _inventory = inventory;
        _loc = loc;
    }

    // Both the top row (its two panels end in a VerticalFill, so they stretch to equal height with their content at the
    // top) and the per-plugin list are flexible: the tab's slack is shared, and nothing overflows at a larger font scale.
    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(BuildRatePanel(), Width: LayoutPanel.ColumnWidth),
                new CellElement(BuildFramePanel(), Width: LayoutPanel.ColumnWidth),
            }, Gap: 8f),
            BuildPluginSection(),
        }, Gap: 8f);
    }

    /// <summary>Header status: "30 Hz · frame cap on".</summary>
    public string Status(SettingsKit k)
        => _loc.TFormat("perf.status", RateValueLabel(), _prefs.Uncap ? k.Bad(_loc.T("perf.cap.off")) : _loc.T("perf.cap.on"));

    private HudElement BuildRatePanel()
    {
        var s = _k.S;
        return _k.Panel("perf.rate", () => _prefs.UpdateRateHz == PerfControls.DefaultUpdateRateHz ? _loc.T("perf.recommended") : "",
            _k.Description("perf.rate.desc"),
            SettingsKit.Row(
                new CellElement(SettingsKit.Slider(RateToSlider, SliderToRate, 0f, RateStops.Length - 1), Weight: 1f),
                new TextElement(RateValueLabel, Color: () => s.Text(), Width: ValueWidth, NoWrap: true) { Bold = true },
                _k.Help("perf.rate", "perf.rate", "help.perf.rate", "tab.performance")),
            TickRow(),
            StellarStyle.MinHeight(RangeDescHeight, s.MutedLine(RateDescription)),
            StellarStyle.VerticalFill());
    }

    private HudElement BuildFramePanel()
    {
        var s = _k.S;
        return _k.Panel("perf.frame", null,
            SettingsKit.Row(_k.Label(_k.T("perf.uncap.label")),
                SettingsKit.Toggle(() => _prefs.Uncap, SetUncap),
                _k.Help("perf.uncap", "perf.uncap.label", "help.perf.uncap", "tab.performance")),
            StellarStyle.MinHeight(RangeDescHeight, new ConditionalElement(() => _prefs.Uncap,
                new TextElement(_k.T("perf.uncap.warn"), Color: () => s.Readable(s.Caution(), s.Panel())),
                s.MutedLine(_k.T("perf.uncap.capped")))),
            StellarStyle.VerticalFill());
    }

    // The stop labels under the slider, spread edge to edge (flexible spacers between them = space-between), the current
    // one in the accent. All bold-11 (only the styled path honours a font size; bold can't toggle live, so the current
    // one differs by colour only).
    private HudElement TickRow()
    {
        var s = _k.S;
        var items = new System.Collections.Generic.List<HudElement>();
        for (var i = 0; i < RateStops.Length; i++)
        {
            var idx = i;
            if (i > 0) items.Add(new SpacerElement());
            items.Add(new TextElement(() => TickLabel(idx),
                Color: () => (int)RateToSlider() == idx ? s.Readable(s.Accent(), s.Panel()) : s.Muted(),
                NoWrap: true, FontSize: (int)TickFontSize) { Bold = true });
        }
        return new RowElement(new HudElement[]
        {
            new CellElement(new RowElement(items, Gap: 0.01f), Weight: 1f),
            // Same width as [value, "?"] + their gaps in the slider row, so the labels span the slider track only.
            new SpacerElement(ValueWidth + StellarHelpPopover.ButtonSize + 16f, 1f),
        }, Gap: 8f);
    }

    private string TickLabel(int i) => RateStops[i] >= PerfControls.MaxUpdateRateHz ? _loc.T("perf.tick.max") : RateStops[i].ToString();

    private void SetUncap(bool on)
    {
        _prefs.Uncap = on;
        _k.Done(_loc.T(on ? "perf.fb.uncapped" : "perf.fb.capped"));
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
        if (hz <= 15) return _loc.T("perf.range.low");
        if (hz >= PerfControls.MaxUpdateRateHz) return _loc.T("perf.range.max");
        if (hz <= PerfControls.DefaultUpdateRateHz) return _loc.T("perf.range.default");
        return _loc.T("perf.range.high");
    }
}
