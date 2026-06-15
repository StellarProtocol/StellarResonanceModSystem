using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Plugins list. One row per discovered plugin: enable toggle,
/// plugin name, right-aligned version, optional Retry button on errored rows.
/// Toggle click routes through the inventory's <c>SetEnabled</c> path
/// (Disposes + reconstructs from the same factory — soft cycle).
/// </summary>
internal sealed class PluginsPanel
{
    private readonly IPluginInventory _inventory;
    private readonly ITheme _theme;
    private readonly System.Action<string, bool> _setEnabled;

    public PluginsPanel(IPluginInventory inventory, ITheme theme, System.Action<string, bool> setEnabled)
    {
        _inventory = inventory;
        _theme = theme;
        _setEnabled = setEnabled;
    }

    private const int MaxRows = 64;
    private const float NameColumnWidth = 280f;   // fixed name column → versions align in a clean column
    private static readonly ColorRgba MutedColor = new(0.52f, 0.58f, 0.66f, 0.9f);
    // Live snapshot of the inventory, refreshed once per apply by the outer Conditional's When (which runs in
    // the Conds phase, before the List/row Funcs). Lets the list track plugins discovered AFTER the hub is built.
    private IReadOnlyList<PluginInfo> _cache = System.Array.Empty<PluginInfo>();

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/>. A LIVE list (not a build-time snapshot — that
    /// was empty because the hub is built before plugins are discovered): MaxRows slots, the first
    /// <c>_cache.Count</c> shown, each reading the i-th plugin live (toggle + name + version/Retry). When there
    /// are NO plugins, a muted "No plugins loaded." message is shown instead of an empty scroll.</summary>
    public HudElement Describe()
    {
        var slots = new HudElement[MaxRows];
        for (var i = 0; i < MaxRows; i++) slots[i] = BuildPluginRow(i);
        var list = new ListElement(() => _cache.Count, slots);
        return new ConditionalElement(
            () => { _cache = _inventory.List(); return _cache.Count > 0; },
            new ScrollElement(list, Height: 260f),
            new TextElement(() => "No plugins loaded.", () => MutedColor));
    }

    private HudElement BuildPluginRow(int idx)
    {
        PluginInfo? At() => idx < _cache.Count ? _cache[idx] : null;
        // Fixed-width name column so the version (after it) lines up in a clean column regardless of name length.
        return new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => At()?.IsEnabled ?? false,
                v => { var p = At(); if (p != null) _setEnabled(p.Id, v); }),
            new TextElement(() => At()?.DisplayName ?? "", Width: NameColumnWidth),
            new ConditionalElement(() => At()?.IsErrored ?? false,
                new ButtonElement(() => "Retry", () => { var p = At(); if (p != null) _inventory.RequestRetry(p.Id); }),
                new TextElement(() => At()?.Version ?? "")),
        }, Gap: 10f);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
