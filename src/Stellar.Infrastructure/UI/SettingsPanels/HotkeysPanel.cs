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
///
/// Rows group by the OWNING plugin (<see cref="IHotkeyAction.PluginId"/>, set by the host's
/// per-plugin <c>IHotkeys</c>) and the header shows that plugin's <c>PluginInfo.DisplayName</c>.
/// The id-prefix fallback remains load-bearing for framework actions, whose PluginId is null
/// because they are declared straight on the shared hotkey service.
/// </summary>
internal sealed partial class HotkeysPanel
{
    private enum Filter { All, Plugins, Framework }

    private readonly IHotkeyDirectory _directory;
    private readonly IHotkeyBlockDirectory _blockDirectory;
    private readonly IPluginInventory _inventory;
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

    public HotkeysPanel(IHotkeyDirectory directory, IHotkeyBlockDirectory blockDirectory, IPluginInventory inventory, ITheme theme)
    {
        _directory = directory;
        _blockDirectory = blockDirectory;
        _inventory = inventory;
        _theme = theme;
        // Invalidate the cached label + sort snapshot when a binding changes;
        // the next OnGUI pass rebuilds whatever it needs.
        _directory.BindingChanged += OnBindingChanged;
        // Group HEADERS show the plugin's DisplayName, which only becomes the real
        // declared name ("Mahiru Utility") once the registry enables the plugin —
        // and plugins load AFTER the hub is built. Without this subscription the
        // header would keep showing the seeded assembly name until the user happened
        // to toggle a filter chip (the only other RebuildDisplay trigger).
        _inventory.StatusChanged += OnPluginStatusChanged;
    }

    private void OnBindingChanged(string actionId)
    {
        _bindingLabelCache.Remove(actionId);
    }

    private void OnPluginStatusChanged(PluginInfo _)
    {
        _pluginNamesDirty = true;
        _inventoryVersion++;   // forces RebuildDisplay to re-run so headers pick up the new label
    }

    public bool IsCapturing => _capturingActionId is not null;

    private const int MaxRows = 64;
    // Fixed width for the action-row label. Rows show the plugin-authored HotkeyAction.Description,
    // which runs to ~40 chars ("Cycle CombatMeter metric (DPS/HPS/Taken)") and WRAPPED to two lines,
    // making row heights uneven inside the 260f scroll. Width + NoWrap clips instead (a fixed-width
    // NoWrap TextElement gets its own RectMask2D, so it truncates inside its own cell rather than
    // spilling onto the binding cell) — see WindowBuilder-Patterns.md.
    //
    // 315f is the HARD ceiling, measured not guessed: 567f usable row width (600f hub − 24f GlassMenu
    // body padding − 9f scroll viewport inset for the scrollbar) − 18f indent − 210f binding cell −
    // 3 × 8f row gaps. Two traps in that sum: RowElement's gap DEFAULTS to 8f (the builder maps
    // Gap: 0f → RowGap, so a zero gap is not expressible), and a Width > 0 TextElement is pinned
    // (minWidth == preferredWidth, flexibleWidth 0) so it CANNOT be squeezed — go over 315f and the
    // summed minWidths overflow the viewport and the mask clips the binding cell instead.
    // 300f leaves ~15f of visible spacer so the row still reads label-left / cell-right.
    private const float RowLabelWidth = 300f;
    // Flattened display list rebuilt once per apply (in the list's outer Conditional When, which runs before the
    // slot Funcs): one HEADER row per plugin group + an action row per binding (omitted when the group is
    // collapsed). Lets the list track hotkeys DECLARED AFTER the hub is built (plugins load post-wiring) AND
    // group them by plugin with collapsible headers instead of a flat "combatmeter.xxx · combatmeter.yyy" list.
    private readonly List<HkRow> _display = new();
    // Keyed on GroupKey (the stable plugin guid), NEVER GroupLabel — the label mutates at
    // runtime when a plugin enables after the hub was built, which would orphan the entry
    // and silently re-expand a group the user collapsed.
    private readonly HashSet<string> _collapsed = new();   // groups the user collapsed (empty = all expanded)

    private readonly struct HkRow
    {
        public readonly bool IsHeader;
        /// <summary>Stable identity: the owning plugin's guid, or the id prefix for framework actions.</summary>
        public readonly string GroupKey;
        /// <summary>Display text: the plugin's DisplayName when known, else the id prefix.</summary>
        public readonly string GroupLabel;
        public readonly IHotkeyAction? Action;
        public readonly int Count;   // header only: number of actions in the group
        public HkRow(bool isHeader, string groupKey, string groupLabel, IHotkeyAction? action, int count)
        { IsHeader = isHeader; GroupKey = groupKey; GroupLabel = groupLabel; Action = action; Count = count; }
    }

    private readonly Dictionary<string, int> _groupCounts = new();

    // guid → PluginInfo.DisplayName. Rebuilt ONLY when IPluginInventory.StatusChanged fires —
    // never per row per frame. This panel is deliberately de-allocated (see _sortedActionsCache /
    // _filteredScratch / _bindingLabelCache): calling _inventory.List() from a row lambda would
    // put a lookup on every row of every apply pass.
    private readonly Dictionary<string, string> _pluginNames = new();
    private bool _pluginNamesDirty = true;
    private int _inventoryVersion;
    private int _builtInventoryVersion = -1;

    private Dictionary<string, string> PluginNames()
    {
        if (!_pluginNamesDirty) return _pluginNames;
        _pluginNamesDirty = false;
        _pluginNames.Clear();
        foreach (var p in _inventory.List())
            if (!string.IsNullOrWhiteSpace(p.DisplayName)) _pluginNames[p.Id] = p.DisplayName;
        return _pluginNames;
    }

    /// <summary>Settings → Hotkeys, GROUPED by plugin with collapsible headers (a LIVE list — the hub is built
    /// before plugins load, so it can't be a build-time snapshot): <see cref="MaxRows"/> slots over a flattened
    /// header/row list rebuilt each apply. A header toggles its group's collapse and shows the plugin's own
    /// declared name; an action row shows its Description + binding cell. Click a cell to capture; Del clears / Esc cancels
    /// (<see cref="PollCaptureUgui"/>). Filter chips drive <see cref="_filter"/>.</summary>
    public HudElement Describe()
    {
        var slots = new HudElement[MaxRows];
        for (var i = 0; i < MaxRows; i++) slots[i] = BuildHotkeySlot(i);
        var list = new ListElement(() => _display.Count, slots);
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", Get: () => _blockDirectory.GetBlockAllFromGame(), Set: v => _blockDirectory.SetBlockAllFromGame(v)),
                new TextElement(() => "Block hotkeys from game"),
            }, Gap: 6f),
            new SeparatorElement(),
            new RowElement(new HudElement[] { FilterChip("All", Filter.All), FilterChip("Plugins", Filter.Plugins), FilterChip("Framework", Filter.Framework) }),
            new ConditionalElement(
                () => { RebuildDisplay(); return _display.Count > 0; },
                new ScrollElement(list, Height: 260f),
                new TextElement(() => "No hotkeys.", () => _theme.Colors.TextMuted)),
            new ButtonElement(() => "Reset all to defaults", () => ResetAllToDefaults()),
        });
    }

    // Rebuild the flattened header/row list from the live (sorted + filtered) actions. OrderedActions sorts by
    // GroupKey first, so same-group actions are adjacent and a new header starts whenever the key changes.
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
        // _inventoryVersion is in the guard because group LABELS are snapshotted into HkRow at
        // rebuild time; a plugin enabling later changes its DisplayName and must re-flatten.
        var count = _directory.Actions.Count;
        if (_display.Count > 0 && count == _lastActionCount && _filter == _lastBuiltFilter
            && _collapseVersion == _builtCollapseVersion && _inventoryVersion == _builtInventoryVersion) return;
        _lastActionCount = count; _lastBuiltFilter = _filter; _builtCollapseVersion = _collapseVersion;
        _builtInventoryVersion = _inventoryVersion;

        _display.Clear();
        var actions = OrderedActions();
        var names = PluginNames();
        _groupCounts.Clear();
        foreach (var a in actions) { var g = GroupKeyOf(a); _groupCounts[g] = _groupCounts.TryGetValue(g, out var c) ? c + 1 : 1; }
        string? cur = null;
        foreach (var a in actions)
        {
            var key = GroupKeyOf(a);
            if (key != cur)
            {
                cur = key;
                _display.Add(new HkRow(true, key, GroupLabelOf(a, names), null, _groupCounts[key]));
            }
            if (!_collapsed.Contains(key)) _display.Add(new HkRow(false, key, "", a, 0));
        }
    }

    /// <summary>Stable group identity. Uses the owning plugin's guid when the action was declared
    /// through a per-plugin <c>IHotkeys</c>; falls back to the id prefix for framework actions
    /// (declared straight on the shared service, so PluginId is null).</summary>
    private static string GroupKeyOf(IHotkeyAction a)
        => string.IsNullOrEmpty(a.PluginId) ? GroupOf(a.Id) : a.PluginId!;

    /// <summary>Header text for a group: the plugin's own declared name, else the id prefix
    /// (which is what framework actions and any not-yet-inventoried plugin resolve to).</summary>
    private static string GroupLabelOf(IHotkeyAction a, Dictionary<string, string> names)
        => names.TryGetValue(GroupKeyOf(a), out var n) ? n : GroupOf(a.Id);

    /// <summary>Row text: the declared human-readable description, falling back to the
    /// prefix-stripped id for actions that shipped without one.</summary>
    private static string RowLabel(IHotkeyAction? a)
    {
        if (a is null) return "";
        return string.IsNullOrWhiteSpace(a.Description) ? ShortName(a.Id) : a.Description;
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
                        new TextElement(() => _collapsed.Contains(Row().GroupKey) ? "▶" : "▼", () => _theme.Colors.Accent, Width: 16f),
                        new TextElement(() => Row().GroupLabel, Emphasis: true),
                        new SpacerElement(),
                        new TextElement(() => $"({Row().Count})", () => _theme.Colors.TextMuted, Align: TextAlign.Right),
                    }),
                    // Collapse state keys on GroupKey — the label is display-only and mutates.
                    OnClick: () => ToggleGroup(Row().GroupKey))),
            // Action row — indented description (clipped to one line) + binding cell.
            new ConditionalElement(() => idx < _display.Count && !_display[idx].IsHeader,
                new RowElement(new HudElement[]
                {
                    new SpacerElement(Width: 18f),
                    new TextElement(() => RowLabel(Row().Action), Width: RowLabelWidth, NoWrap: true),
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
                if (aFw != bFw) return aFw ? 1 : -1;   // framework actions always sort last
                // GroupKey before Id: RebuildDisplay starts a new header whenever the key
                // changes, so same-group rows MUST be adjacent. Sorting on Id alone was only
                // adjacent by luck — a plugin declaring two different id prefixes (or two
                // plugins sharing one) would emit duplicate headers for the same group.
                var g = string.Compare(GroupKeyOf(a), GroupKeyOf(b), System.StringComparison.Ordinal);
                if (g != 0) return g;
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
