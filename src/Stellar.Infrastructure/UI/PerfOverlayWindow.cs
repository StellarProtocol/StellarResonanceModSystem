using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Dev-only perf-harness readout + bisect controls, as a native uGUI window (Phase E — no IMGUI). Registered
/// through <see cref="IWindowHost"/> by the Host; auto-shown when <see cref="PerfProbe.IsEnabled"/>; Shift+End
/// toggles. Live readout (FPS/frame/draw/update), the three bisect toggles, the throttle stepper, and the
/// per-window cur/min/max ms table. The element tree is built once; Funcs re-pull on the window's capped refresh.
/// </summary>
internal sealed class PerfOverlayWindow
{
    private const int TopN = 12;
    private const float ColW = 52f;   // cur/min/max numeric column width
    private string[] _muteKeys = Array.Empty<string>();   // top-N window ids, refreshed each poll
    // Per-window ms kept across polls (apply timing publishes only on apply frames ~10 Hz, so a raw
    // Snapshot().WindowMs is empty on most polls). Entries are NEVER evicted — every window that has rendered
    // stays listed (cur = latest; min/max = its range), so a since-closed window still shows its history.
    private readonly Dictionary<string, double> _lastMs = new();
    private readonly Dictionary<string, double> _minMs = new();
    private readonly Dictionary<string, double> _maxMs = new();
    private readonly Dictionary<string, double> _sumMs = new();   // running sum + count → avg (since first listed)
    private readonly Dictionary<string, int> _cntMs = new();

    // Sort state — click a column header to sort by it; click again to flip direction.
    private enum SortKey { Name, Cur, Min, Max, Avg }
    private SortKey _sortKey = SortKey.Cur;
    private bool _sortDesc = true;
    private void SetSort(SortKey k) { if (_sortKey == k) _sortDesc = !_sortDesc; else { _sortKey = k; _sortDesc = k != SortKey.Name; } }

    public WindowRegistration BuildRegistration()
    {
        var spec = new WindowSpec("stellar.perf-overlay", "Stellar Perf",
            new WindowRect(1621f, 275f, 460f, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
        { StartVisible = PerfProbe.IsEnabled, Draggable = true };

        var readout = new ColumnElement(new HudElement[]
        {
            new TextElement(() => { var s = PerfProbe.Snapshot(); return $"FPS {s.Fps,5:0.0}   frame {s.LastFrameMs,5:0.00} ms"; }),
            new TextElement(() => $"draw CPU   {PerfProbe.Snapshot().LastDrawMs,6:0.000} ms / frame"),
            new TextElement(() => $"update CPU {PerfProbe.Snapshot().LastUpdateMs,6:0.000} ms / frame"),
        });

        var toggles = new ColumnElement(new HudElement[]
        {
            ToggleRow("Master HUD kill", () => PerfControls.MasterHudKill, v => PerfControls.MasterHudKill = v),
            ToggleRow("Chrome kill",     () => PerfControls.ChromeKill,    v => PerfControls.ChromeKill = v),
            ToggleRow("Force opaque",    () => PerfControls.ForceOpaque,   v => PerfControls.ForceOpaque = v),
        });

        var throttle = new RowElement(new HudElement[]
        {
            new TextElement(() => $"Throttle 1/{PerfControls.ThrottleN}", Width: 110f),
            new ButtonElement(() => "-", () => { if (PerfControls.ThrottleN > 1) PerfControls.ThrottleN--; }, Width: 30f),
            new ButtonElement(() => "+", () => PerfControls.ThrottleN++, Width: 30f),
        }, Gap: 4f);

        var root = new ColumnElement(new HudElement[]
        {
            readout, new SeparatorElement(), toggles, throttle,
            new SeparatorElement(), BuildMuteSection(),
        }, Gap: 4f);
        return new WindowRegistration(spec, root);
    }

    // Per-window cur/min/max table (toggle to mute). Numeric columns are right-anchored via a flexible spacer
    // so they align between the header and the rows regardless of the window-name/toggle width.
    private HudElement BuildMuteSection()
    {
        // Clickable column headers — sort by that column; the active one shows a v/^ direction marker.
        var header = new RowElement(new HudElement[]
        {
            HeaderBtn("window", SortKey.Name, 0f),
            new SpacerElement(),
            HeaderBtn("cur", SortKey.Cur, ColW),
            HeaderBtn("avg", SortKey.Avg, ColW),
            HeaderBtn("min", SortKey.Min, ColW),
            HeaderBtn("max", SortKey.Max, ColW),
        }, Gap: 2f);

        var slots = new HudElement[TopN];
        for (var i = 0; i < TopN; i++)
        {
            var idx = i;
            slots[idx] = new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", () => MuteState(idx), v => SetMute(idx, v)),
                new TextElement(() => MuteName(idx)),
                new SpacerElement(),
                new TextElement(() => Cell(idx, _lastMs), Width: ColW, Align: TextAlign.Right),
                new TextElement(() => AvgCell(idx), Width: ColW, Align: TextAlign.Right),
                new TextElement(() => Cell(idx, _minMs), Width: ColW, Align: TextAlign.Right),
                new TextElement(() => Cell(idx, _maxMs), Width: ColW, Align: TextAlign.Right),
            }, Gap: 2f);
        }
        var list = new ListElement(() => Math.Min(_muteKeys.Length, TopN), slots);
        return new ColumnElement(new HudElement[] { header, list }, Gap: 2f);
    }

    /// <summary>Merge this poll's per-window ms into the kept maps (cur/min/max) and re-rank the top-N by current.
    /// Called each poll by the Host while the harness is enabled. Never evicts — a closed window keeps showing.</summary>
    public void RefreshTopWindows()
    {
        foreach (var kv in PerfProbe.Snapshot().WindowMs)
        {
            _lastMs[kv.Key] = kv.Value;
            _minMs[kv.Key] = _minMs.TryGetValue(kv.Key, out var mn) ? Math.Min(mn, kv.Value) : kv.Value;
            _maxMs[kv.Key] = _maxMs.TryGetValue(kv.Key, out var mx) ? Math.Max(mx, kv.Value) : kv.Value;
            _sumMs[kv.Key] = (_sumMs.TryGetValue(kv.Key, out var s) ? s : 0) + kv.Value;
            _cntMs[kv.Key] = (_cntMs.TryGetValue(kv.Key, out var c) ? c : 0) + 1;
        }
        IEnumerable<string> keys = _lastMs.Keys;
        keys = _sortKey == SortKey.Name
            ? (_sortDesc ? keys.OrderByDescending(k => k, StringComparer.Ordinal) : keys.OrderBy(k => k, StringComparer.Ordinal))
            : (_sortDesc ? keys.OrderByDescending(SortVal) : keys.OrderBy(SortVal));
        _muteKeys = keys.Take(TopN).ToArray();
    }

    private double Avg(string k) => _cntMs.TryGetValue(k, out var c) && c > 0 ? _sumMs[k] / c : 0;
    private double SortVal(string k) => _sortKey switch
    {
        SortKey.Min => _minMs.TryGetValue(k, out var mn) ? mn : 0,
        SortKey.Max => _maxMs.TryGetValue(k, out var mx) ? mx : 0,
        SortKey.Avg => Avg(k),
        _           => _lastMs.TryGetValue(k, out var v) ? v : 0,
    };

    private static HudElement ToggleRow(string label, Func<bool> get, Action<bool> set)
        => new RowElement(new HudElement[] { new ToggleElement(() => "", get, set), new TextElement(() => label) }, Gap: 4f);

    // A sortable column header: clicking sorts by `key` (re-click flips direction); the active one shows v/^.
    private HudElement HeaderBtn(string text, SortKey key, float width)
        => new ButtonElement(
            () => _sortKey == key ? $"{text} {(_sortDesc ? "v" : "^")}" : text,
            () => SetSort(key), Active: () => _sortKey == key, Width: width);

    private bool MuteState(int i) => i < _muteKeys.Length && PerfControls.IsMuted(_muteKeys[i]);
    private void SetMute(int i, bool on) { if (i < _muteKeys.Length) PerfControls.SetMuted(_muteKeys[i], on); }
    private string MuteName(int i) => i < _muteKeys.Length ? _muteKeys[i] : string.Empty;   // mute state shown by the toggle
    private string Cell(int i, Dictionary<string, double> src)
        => i < _muteKeys.Length && src.TryGetValue(_muteKeys[i], out var v) ? v.ToString("0.000") : string.Empty;
    private string AvgCell(int i)
        => i < _muteKeys.Length && _cntMs.TryGetValue(_muteKeys[i], out var c) && c > 0 ? (_sumMs[_muteKeys[i]] / c).ToString("0.000") : string.Empty;
}
