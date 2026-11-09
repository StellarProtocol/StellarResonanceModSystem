using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// "Per-plugin rate" for Settings → Performance: a column header (Plugin / Rate / Self-rate) over one row per loaded
/// plugin — name (muted while the plugin is off), a Rate Pill dropdown (Follow global, 10–120 Hz, Every frame) mapped
/// onto the per-plugin override, and a Self-rate Pill dropdown (Off / Boost / Self-managed). The old slider + "→ 60 Hz"
/// ramp read-out is gone (the game font draws "→" blank); the dropdown shows the configured rate.
/// </summary>
internal sealed partial class PerformancePanel
{
    // Per-plugin rate options. Index 0 = "Follow global" (stored as 0); the rest are exact stored Hz values.
    private static readonly int[] PluginStops =
    {
        0, 10, 15, PerfControls.DefaultUpdateRateHz, 60, 120, PerfControls.MaxUpdateRateHz,
    };

    // Pooled rows of the VIRTUAL per-plugin list: no cap on the plugin count (the old 64-row eager pool silently
    // dropped plugin 65+). The flexible viewport starts at 7 rows; the pool covers a viewport up to ~16 rows tall.
    private const int PluginPoolRows = 18;
    private int _pluginFirst;   // first logical row in the pool (VirtualListElement.OnWindow)
    private const int ReservedPluginRows = 7;
    // A Pill dropdown trigger is a fixed 28 px, so these rows are 28 tall (pitch 30) instead of the 26 + 2 tiles.
    private const float PluginRowPitch = 30f;
    // Trigger caption = width − 38 (12 + 12 insets, 6 gap, 8 chevron). Rate 156 → 118 px: "Follow global" (~88 px bold
    // 13) fits with room, the longest locale ("Sundan ang global", fil) about fits and otherwise ellipsizes. Self-rate
    // 128 → 90 px: "Self-managed" / "Kelola sendiri". The header cells reuse these widths, so the columns stay aligned.
    private const float RateDropWidth = 156f;
    private const float SelfDropWidth = 128f;

    // Refreshed each apply by the list's Conditional When: loaded plugins only (a plugin that failed to start has no
    // rate to set; it lives on the Plugins tab with its Retry).
    private readonly List<PluginInfo> _pluginCache = new();

    internal HudElement BuildPluginSection()
    {
        var slots = new HudElement[PluginPoolRows];
        for (var i = 0; i < PluginPoolRows; i++)
        {
            var slot = i;
            // A virtual-list slot stretches its child to the pitch, so the 2 px gap is a spacer in the slot (R15).
            slots[i] = new ColumnElement(new HudElement[]
            {
                BuildPluginRow(() => _pluginFirst + slot),
                new SpacerElement(0f, PluginRowPitch - 28f),
            }, Gap: 0.01f);
        }
        var list = new ConditionalElement(
            () => { RefreshPlugins(); return _pluginCache.Count > 0; },
            new VirtualListElement(() => _pluginCache.Count, PluginRowPitch, slots, first => _pluginFirst = first,
                Height: ReservedPluginRows * PluginRowPitch - 2f),
            _k.S.MutedLine(_k.T("common.noPlugins")));
        return _k.S.SectionPanelWithTrailing(_k.T("perf.perPlugin.title"), null,
            _k.Help("perf.perPlugin", "perf.perPlugin.title", "help.perf.perPlugin", "tab.performance"),
            ColumnHeader(), list);
    }

    private void RefreshPlugins()
    {
        _pluginCache.Clear();
        foreach (var p in _inventory.List()) if (!p.IsErrored) _pluginCache.Add(p);
    }

    // Plugin / Rate / Self-rate, aligned over the row columns (tile inset 6, and the scroll viewport's 9 px bar lane).
    private HudElement ColumnHeader()
    {
        var s = _k.S;
        HudElement H(string key, float width) => new TextElement(_k.T(key), Color: () => s.Muted(), Width: width,
            NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true };
        return new RowElement(new HudElement[]
        {
            new SpacerElement(StellarStyle.TilePadX, 16f),
            new CellElement(new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(_k.T("perf.col.plugin"), Color: () => s.Muted(), NoWrap: true, FontSize: 11)
                    { Bold = true }, Weight: 1f),
                H("perf.col.rate", RateDropWidth),
                H("perf.col.self", SelfDropWidth),
            }, Gap: 8f), Weight: 1f),
            new SpacerElement(StellarStyle.TilePadX + 9f, 1f),
        }, Gap: 0.01f);
    }

    // Display only: the rate prefs, ramp state and list order stay keyed on the plugin Id.
    private string NameOf(PluginInfo? p)
        => p is null ? "" : DisplayNames?.Resolve(p.Id, p.DisplayName) ?? p.DisplayName;

    private HudElement BuildPluginRow(Func<int> idx)
    {
        PluginInfo? At() => idx() < _pluginCache.Count ? _pluginCache[idx()] : null;
        string Id() => At()?.Id ?? "";
        var s = _k.S;
        return s.ListTile(StellarStyle.TileRow(
            _k.LineLabel(() => NameOf(At()), () => At()?.IsEnabled == true ? s.Text() : s.Muted()),
            SettingsKit.Dropdown(() => RateIndex(Id()), () => RateOptions, i => SetRate(At(), i), RateDropWidth),
            SettingsKit.Dropdown(() => SelfRateIndex(Id()), () => SelfRateOptions, i => SetSelfRate(At(), i), SelfDropWidth)));
    }

    // --- rate dropdown <-> stored Hz (index 0 = follow global; a non-stop stored value maps to the nearest stop) ---
    private int RateIndex(string id)
    {
        var hz = _prefs.GetPluginRate(id);            // 0 = follow global
        if (hz <= 0) return 0;
        var best = 1; var bestDist = int.MaxValue;
        for (var i = 1; i < PluginStops.Length; i++)
        {
            var d = Math.Abs(PluginStops[i] - hz);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private void SetRate(PluginInfo? p, int index)
    {
        if (p == null) return;
        index = Math.Clamp(index, 0, PluginStops.Length - 1);
        _prefs.SetPluginRate(p.Id, PluginStops[index]);
        _k.Done(_loc.TFormat("perf.fb.rate", NameOf(p), RateOptions[index]));
    }

    // Cached so the per-frame dropdown poll doesn't allocate; rebuilt only when the active language changes.
    private string[]? _rateCache;
    private string? _rateLang;
    private IReadOnlyList<string> RateOptions
    {
        get
        {
            if (_rateCache == null || _rateLang != _loc.Language)
            {
                _rateLang = _loc.Language;
                var opts = new string[PluginStops.Length];
                opts[0] = _loc.T("perf.followGlobal");
                for (var i = 1; i < PluginStops.Length; i++)
                    opts[i] = PluginStops[i] >= PerfControls.MaxUpdateRateHz ? _loc.T("perf.everyFrame") : _loc.TFormat("perf.hz", PluginStops[i]);
                _rateCache = opts;
            }
            return _rateCache;
        }
    }

    // --- Self-rate dropdown: the three modes (indices 0/1/2) map onto the two backing bools (Sustained requires
    // Self-control):
    //   Off (0)          = (selfControl=false, sustained=false)  — follows the global / per-plugin rate
    //   Boost (1)        = (selfControl=true,  sustained=false)  — may ramp up, released after a 10 s safety cap
    //   Self-managed (2) = (selfControl=true,  sustained=true)   — plugin fully controls + holds its rate, no cap
    private string[]? _selfRateCache;
    private string? _selfRateLang;
    private IReadOnlyList<string> SelfRateOptions
    {
        get
        {
            if (_selfRateCache == null || _selfRateLang != _loc.Language)
            {
                _selfRateLang = _loc.Language;
                _selfRateCache = new[] { _loc.T("perf.self.off"), _loc.T("perf.self.boost"), _loc.T("perf.self.managed") };
            }
            return _selfRateCache;
        }
    }

    private int SelfRateIndex(string id)
    {
        if (!_prefs.GetPluginSelfControl(id)) return 0;          // Off
        return _prefs.GetPluginSustained(id) ? 2 : 1;           // Self-managed : Boost
    }

    private void SetSelfRate(PluginInfo? p, int index)
    {
        if (p == null) return;
        var id = p.Id;
        switch (index)
        {
            case 1:  _prefs.SetPluginSelfControl(id, true);  _prefs.SetPluginSustained(id, false); break; // Boost
            case 2:  _prefs.SetPluginSelfControl(id, true);  _prefs.SetPluginSustained(id, true);  break; // Self-managed
            default: _prefs.SetPluginSelfControl(id, false); _prefs.SetPluginSustained(id, false); index = 0; break; // Off
        }
        _k.Done(_loc.TFormat("perf.fb.self", NameOf(p), SelfRateOptions[index]));
    }
}
