using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Hotkeys list. Click a binding cell to enter capture mode; the
/// next non-modifier <see cref="EventType.KeyDown"/> commits the new binding
/// (with held modifiers). Esc cancels capture. Filter chips toggle between
/// All / Plugins / Framework actions.
/// </summary>
internal sealed partial class HotkeysPanel
{
    private enum Filter { All, Plugins, Framework }

    private readonly IHotkeyDirectory _directory;
    private readonly ITheme _theme;
    private Filter _filter = Filter.All;
    private string? _capturingActionId;
    // Last drawn screen rect of the [ … ] cell that initiated the active
    // capture. Captured during DrawRow when the row's action matches
    // _capturingActionId; consumed by TryCancelOnClickOutside to decide
    // whether a mouse click counts as "outside the cell".
    // Sorted snapshot of _directory.Actions cached across frames. The
    // OrderedActions iterator previously sorted + filtered every OnGUI pass,
    // allocating a fresh list each time. Invalidated when bindings change
    // (BindingChanged) or the filter chip toggles.
    private List<IHotkeyAction>? _sortedActionsCache;
    // Cached "Shift+Home" string per action.Id — recomputed only on
    // BindingChanged for that id (or when the row first appears). Avoids the
    // per-row string interpolation under DrawRow.
    private readonly Dictionary<string, string> _bindingLabelCache = new();

    private HudElement FilterChip(string label, Filter f)
        => new ButtonElement(() => label, () => { _filter = f; }, null, null, Active: () => _filter == f);

    private bool FilterMatches(bool isFramework)
        => _filter == Filter.All || (_filter == Filter.Plugins && !isFramework) || (_filter == Filter.Framework && isFramework);

    public HotkeysPanel(IHotkeyDirectory directory, ITheme theme)
    {
        _directory = directory;
        _theme = theme;
        // Invalidate the cached label + sort snapshot when a binding changes;
        // the next OnGUI pass rebuilds whatever it needs.
        _directory.BindingChanged += OnBindingChanged;
    }

    private void OnBindingChanged(string actionId)
    {
        _bindingLabelCache.Remove(actionId);
    }

    public bool IsCapturing => _capturingActionId is not null;

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration). One row per
    /// action (id + binding-cell button) with a Framework divider + Reset-all. Click a cell to capture;
    /// <see cref="PollCaptureUgui"/> (Host-ticked) commits the next key. Filter chips are a follow-up
    /// (rows are built once; refiltering needs a rebuild). Built once on open.</summary>
    public HudElement Describe()
    {
        var saved = _filter; _filter = Filter.All;
        var all = new System.Collections.Generic.List<IHotkeyAction>(OrderedActions());   // all, sorted
        _filter = saved;

        var rows = new System.Collections.Generic.List<HudElement>
        {
            new RowElement(new HudElement[] { FilterChip("All", Filter.All), FilterChip("Plugins", Filter.Plugins), FilterChip("Framework", Filter.Framework) }),
        };
        var lastFw = false;
        foreach (var action in all)
        {
            var a = action;
            var isFw = a.Id.StartsWith("framework.", System.StringComparison.Ordinal);
            if (isFw && !lastFw)
                rows.Add(new ConditionalElement(() => _filter != Filter.Plugins, new TextElement(() => "--- Framework ---", () => _theme.Colors.TextMuted)));
            lastFw = isFw;
            var row = new RowElement(new HudElement[]
            {
                new TextElement(() => a.Id),
                new SpacerElement(),
                new ButtonElement(
                    () => _capturingActionId == a.Id ? "[ press a key… ]" : GetOrBuildBindingLabel(a),
                    () => ToggleCapture(a.Id), Width: 210f),   // fixed-width cell — never overflows the row (clip fix)
            });
            rows.Add(new ConditionalElement(() => FilterMatches(isFw), row));   // live refilter without rebuild
        }
        return new ColumnElement(new HudElement[]
        {
            new ScrollElement(new ColumnElement(rows.ToArray()), 260f),
            new ButtonElement(() => "Reset all to defaults", () => ResetAllToDefaults()),
        });
    }

    private List<IHotkeyAction> OrderedActions()
    {
        // The full sorted list rarely changes — only when DeclareAction adds a
        // new entry, which fires through IHotkeyDirectory's BindingChanged
        // event for the new action. Cache the sorted snapshot; filter on read.
        // Note: we re-check the cached snapshot's count against the directory
        // each call so a new action that landed without firing BindingChanged
        // for itself still surfaces — a cheap safety net.
        var live = _directory.Actions;
        if (_sortedActionsCache is null || _sortedActionsCache.Count != live.Count)
        {
            var list = new List<IHotkeyAction>(live);
            list.Sort((a, b) =>
            {
                var aFw = a.Id.StartsWith("framework.", System.StringComparison.Ordinal);
                var bFw = b.Id.StartsWith("framework.", System.StringComparison.Ordinal);
                if (aFw != bFw) return aFw ? 1 : -1;
                return string.Compare(a.Id, b.Id, System.StringComparison.Ordinal);
            });
            _sortedActionsCache = list;
        }

        // Filter inline into a reusable buffer so we don't allocate per OnGUI.
        _filteredScratch.Clear();
        foreach (var a in _sortedActionsCache)
        {
            var isFw = a.Id.StartsWith("framework.", System.StringComparison.Ordinal);
            if (_filter == Filter.Plugins && isFw) continue;
            if (_filter == Filter.Framework && !isFw) continue;
            _filteredScratch.Add(a);
        }
        return _filteredScratch;
    }

    // Reused scratch list returned from OrderedActions(). Stable across
    // frames; cleared at the top of each call.
    private readonly List<IHotkeyAction> _filteredScratch = new();

    private string GetOrBuildBindingLabel(IHotkeyAction action)
    {
        if (_bindingLabelCache.TryGetValue(action.Id, out var cached)) return cached;
        var inner = action.CurrentBinding is { } b ? b.ToString() : "unbound";
        var label = $"[ {inner} ]";
        _bindingLabelCache[action.Id] = label;
        return label;
    }

    private void ToggleCapture(string actionId)
    {
        if (_capturingActionId == actionId)
        {
            _capturingActionId = null;
            _directory.EndCapture();
        }
        else
        {
            _capturingActionId = actionId;
            _directory.BeginCapture(actionId);
        }
    }

    private void ResetAllToDefaults()
    {
        foreach (var action in _directory.Actions)
            _directory.Rebind(action.Id, _directory.GetSuggestedDefault(action.Id));
    }
}
