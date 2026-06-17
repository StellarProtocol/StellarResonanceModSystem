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

    private const int MaxRows = 64;
    // Flattened display list rebuilt once per apply (in the list's outer Conditional When, which runs before the
    // slot Funcs): one HEADER row per plugin group + an action row per binding (omitted when the group is
    // collapsed). Lets the list track hotkeys DECLARED AFTER the hub is built (plugins load post-wiring) AND
    // group them by plugin with collapsible headers instead of a flat "combatmeter.xxx · combatmeter.yyy" list.
    private readonly List<HkRow> _display = new();
    private readonly HashSet<string> _collapsed = new();   // groups the user collapsed (empty = all expanded)

    private readonly struct HkRow
    {
        public readonly bool IsHeader;
        public readonly string Group;
        public readonly IHotkeyAction? Action;
        public readonly int Count;   // header only: number of actions in the group
        public HkRow(bool isHeader, string group, IHotkeyAction? action, int count) { IsHeader = isHeader; Group = group; Action = action; Count = count; }
    }

    private readonly Dictionary<string, int> _groupCounts = new();

    /// <summary>Settings → Hotkeys, GROUPED by plugin with collapsible headers (a LIVE list — the hub is built
    /// before plugins load, so it can't be a build-time snapshot): <see cref="MaxRows"/> slots over a flattened
    /// header/row list rebuilt each apply. A header toggles its group's collapse; an action row shows the short
    /// name (plugin prefix stripped) + binding cell. Click a cell to capture; Del clears / Esc cancels
    /// (<see cref="PollCaptureUgui"/>). Filter chips drive <see cref="_filter"/>.</summary>
    public HudElement Describe()
    {
        var slots = new HudElement[MaxRows];
        for (var i = 0; i < MaxRows; i++) slots[i] = BuildHotkeySlot(i);
        var list = new ListElement(() => _display.Count, slots);
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[] { FilterChip("All", Filter.All), FilterChip("Plugins", Filter.Plugins), FilterChip("Framework", Filter.Framework) }),
            new ConditionalElement(
                () => { RebuildDisplay(); return _display.Count > 0; },
                new ScrollElement(list, Height: 260f),
                new TextElement(() => "No hotkeys.", () => _theme.Colors.TextMuted)),
            new ButtonElement(() => "Reset all to defaults", () => ResetAllToDefaults()),
        });
    }

    // Rebuild the flattened header/row list from the live (sorted + filtered) actions. Same-prefix actions are
    // adjacent in the sorted order, so a new header starts whenever the group prefix changes.
    private int _lastActionCount = -1;
    private Filter _lastBuiltFilter;
    private int _collapseVersion;       // bumped on every expand/collapse
    private int _builtCollapseVersion = -1;

    private void RebuildDisplay()
    {
        // Only rebuild when the structure can actually have changed (action set / filter / collapse state).
        // Previously this allocated a list + dict EVERY apply while Settings was open — needless GC churn /
        // frame cost. Binding changes (rebind) don't alter structure (labels are read live), so they don't
        // trip a rebuild.
        var count = _directory.Actions.Count;
        if (_display.Count > 0 && count == _lastActionCount && _filter == _lastBuiltFilter && _collapseVersion == _builtCollapseVersion) return;
        _lastActionCount = count; _lastBuiltFilter = _filter; _builtCollapseVersion = _collapseVersion;

        _display.Clear();
        var actions = OrderedActions();
        _groupCounts.Clear();
        foreach (var a in actions) { var g = GroupOf(a.Id); _groupCounts[g] = _groupCounts.TryGetValue(g, out var c) ? c + 1 : 1; }
        string? cur = null;
        foreach (var a in actions)
        {
            var group = GroupOf(a.Id);
            if (group != cur) { cur = group; _display.Add(new HkRow(true, group, null, _groupCounts[group])); }
            if (!_collapsed.Contains(group)) _display.Add(new HkRow(false, group, a, 0));
        }
    }

    private HudElement BuildHotkeySlot(int idx)
    {
        HkRow Row() => idx < _display.Count ? _display[idx] : default;
        return new ColumnElement(new HudElement[]
        {
            // Plugin header — a clickable row (arrow + bold name + count), NOT a button chip, matching the
            // StatInspector category style. Click anywhere on the row to expand/collapse the group.
            new ConditionalElement(() => idx < _display.Count && _display[idx].IsHeader,
                new SelectableElement(
                    new RowElement(new HudElement[]
                    {
                        new TextElement(() => _collapsed.Contains(Row().Group) ? "▶" : "▼", () => _theme.Colors.Accent, Width: 16f),
                        new TextElement(() => Row().Group, Emphasis: true),
                        new SpacerElement(),
                        new TextElement(() => $"({Row().Count})", () => _theme.Colors.TextMuted, Align: TextAlign.Right),
                    }),
                    OnClick: () => ToggleGroup(Row().Group))),
            // Action row — indented short name + binding cell.
            new ConditionalElement(() => idx < _display.Count && !_display[idx].IsHeader,
                new RowElement(new HudElement[]
                {
                    new SpacerElement(Width: 18f),
                    new TextElement(() => ShortName(Row().Action?.Id)),
                    new SpacerElement(),
                    new ButtonElement(
                        // While capturing, the cell hints the keys: Del clears the binding (unbind), Esc cancels.
                        () => { var a = Row().Action; return a is null ? "" : (_capturingActionId == a.Id ? "[ press a key · Del clears ]" : GetOrBuildBindingLabel(a)); },
                        () => { var a = Row().Action; if (a is not null) ToggleCapture(a.Id); },
                        Width: 210f),   // fixed-width cell — never overflows the row (clip fix)
                })),
        });
    }

    private static string GroupOf(string id) { var i = id.IndexOf('.'); return i < 0 ? id : id.Substring(0, i); }
    private static string ShortName(string? id) { if (id is null) return ""; var i = id.IndexOf('.'); return i < 0 ? id : id.Substring(i + 1); }
    private void ToggleGroup(string group) { if (string.IsNullOrEmpty(group)) return; if (!_collapsed.Remove(group)) _collapsed.Add(group); _collapseVersion++; }

    // Unbind an action (no hotkey). Rebind(null) persists the explicit-unbound state and fires BindingChanged,
    // which drops the cached "[ key ]" label so the cell re-renders as "unbound".
    private void Unbind(string actionId)
    {
        if (_capturingActionId == actionId) CancelCapture();   // clearing the cell we're capturing → stop capture
        _directory.Rebind(actionId, null);
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
