using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Plugins: one SectionPanel "Installed plugins" holding a list of 26 px tiles [icon, name, version, on/off
/// switch]. A plugin that failed to start gets a red tile, a "!" icon, "Couldn't start" and a Retry pill. Toggling routes
/// through the inventory's <c>SetEnabled</c> (Dispose + reconstruct from the same factory — soft cycle) and flashes the
/// row "On"/"Off" with a footer confirmation (R13).
/// </summary>
internal sealed class PluginsPanel
{
    private readonly IPluginInventory _inventory;
    private readonly System.Action<string, bool> _setEnabled;
    private readonly ILocalization _loc;
    private SettingsKit _k = null!;

    // theme is no longer read (the house kit carries the live colours) but the parameter stays so the wiring is
    // unchanged.
    public PluginsPanel(IPluginInventory inventory, ITheme theme, System.Action<string, bool> setEnabled, ILocalization loc)
    {
        _inventory = inventory;
        _setEnabled = setEnabled;
        _loc = loc;
    }

    /// <summary>Display-only plugin names and launcher icons (the plugin's own tile). Set by the wiring as an init
    /// property. Read only from row Funcs (render-driven), never from a per-tick path.</summary>
    internal IPluginDisplayNames? DisplayNames { get; init; }

    // Pooled rows of the VIRTUAL list: no cap on the plugin count (the old 64-row eager pool silently dropped plugin
    // 65+). The pool only covers the tallest viewport (478 px tab body / 28 px pitch ≈ 17, + margin).
    private const int PoolRows = 20;
    private int _first;   // first logical row in the pool (VirtualListElement.OnWindow, set before slot Funcs run)
    private const float IconSize = 18f;
    private const int IconGlyph = 12;       // (18 − 12) / 2 = 3 px padding: whole pixels, centred on the tile
    private const float TrailWidth = 62f;   // version / flash label / switch column
    // Live snapshot of the inventory, refreshed once per apply by the list's Conditional When (Conds run before the
    // row Funcs), so the list tracks plugins discovered AFTER the hub was built.
    private IReadOnlyList<PluginInfo> _cache = System.Array.Empty<PluginInfo>();

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        var slots = new HudElement[PoolRows];
        for (var i = 0; i < PoolRows; i++) { var slot = i; slots[i] = StellarStyle.ListSlot(BuildRow(() => _first + slot)); }
        var list = new ConditionalElement(
            () => { _cache = _inventory.List(); return _cache.Count > 0; },
            // Virtual + flexible viewport: takes the panel's slack, so the list fills the tab down to the footer.
            new VirtualListElement(() => _cache.Count, StellarStyle.ListRowPitch, slots, first => _first = first,
                Height: 120f),
            kit.S.MutedLine(kit.T("common.noPlugins")));
        return kit.Panel("plugins.installed", () => _loc.TFormat("plugins.found", _cache.Count), list);
    }

    /// <summary>Header status: "13 on · 2 off · 1 failed to load".</summary>
    public string Status(SettingsKit k)
    {
        int on = 0, off = 0, failed = 0;
        foreach (var p in _inventory.List())
        {
            if (p.IsErrored) failed++;
            else if (p.IsEnabled) on++;
            if (!p.IsEnabled) off++;
        }
        var s = k.Good(_loc.TFormat("plugins.status.on", on)) + " · " + _loc.TFormat("plugins.status.off", off);
        return failed > 0 ? s + " · " + k.Bad(_loc.TFormat("plugins.status.failed", failed)) : s;
    }

    /// <summary>Any plugin failed to load (the rail's red dot).</summary>
    public bool AnyFailed()
    {
        foreach (var p in _inventory.List()) if (p.IsErrored) return true;
        return false;
    }

    private HudElement BuildRow(System.Func<int> idx)
    {
        PluginInfo? At() => idx() < _cache.Count ? _cache[idx()] : null;
        string Key() => At()?.Id ?? "";
        var s = _k.S;
        var normal = s.ListTile(StellarStyle.TileRow(
                Icon(idx),
                _k.LineLabel(() => NameOf(At())),
                Trailing(Key, () => At()?.Version ?? ""),
                new CellElement(new RowElement(new HudElement[]
                {
                    SettingsKit.Toggle(() => At()?.IsEnabled ?? false, v => Toggle(At(), v)),
                }, Justify: RowJustify.Right), Width: TrailWidth)),
            flashing: () => _k.Flash.IsOn(Key()));
        var failed = s.ListTile(StellarStyle.TileRow(
                s.FilledTile(new TextElement(() => "!", Color: () => StellarStyle.White, Width: IconSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 11) { Bold = true }, s.Danger, IconSize, 5f),
                new CellElement(new RowElement(new HudElement[]
                {
                    new TextElement(() => NameOf(At()), Color: () => s.Text(), NoWrap: true),
                    new TextElement(() => "· " + _loc.T("plugins.couldntStart"), Color: () => s.Muted(), NoWrap: true),
                }, Gap: 6f), Weight: 1f),
                _k.Value(() => At()?.Version ?? "", 54f),
                new CellElement(new RowElement(new HudElement[]
                {
                    s.SmallPill(_k.T("common.retry"), PillKind.Primary, () => Retry(At()), 42f),
                }, Justify: RowJustify.Right), Width: TrailWidth)),
            failed: () => true);
        return new ConditionalElement(() => At()?.IsErrored ?? false, failed, normal);
    }

    // Every row gets the same 18 px accent tile (prototype .lrow .pi, radius 5): the plugin's own launcher glyph tinted
    // white on it when it registered one (as the launcher draws it), else its initial. The rows are a POOLED virtual
    // list (a slot shows another plugin after a scroll), so the image is Live: it re-pulls each refresh and swaps
    // when the PNG array changes (ImageElement.Live, 2.30.0).
    private HudElement Icon(System.Func<int> idx)
    {
        PluginInfo? At() => idx() < _cache.Count ? _cache[idx()] : null;
        var image = _k.S.FilledTile(
            new ImageElement(() => At() is { } p ? DisplayNames?.IconOf(p.Id) : null, IconGlyph, IconGlyph)
                { Tint = () => StellarStyle.White, Live = true },
            _k.S.Accent, IconSize, 5f);
        var initial = _k.S.FilledTile(new TextElement(() => Initial(At()), Color: () => StellarStyle.White, Width: IconSize,
            Align: TextAlign.Center, NoWrap: true, FontSize: 10) { Bold = true }, _k.S.Accent, IconSize, 5f);
        return new CellElement(new ConditionalElement(
            () => At() is { } p && DisplayNames?.IconOf(p.Id) != null,
            image, initial), Width: IconSize);
    }

    // Version (muted) — or, for ~1 s after a toggle, the bold green "On"/"Off" flash in the same column.
    private HudElement Trailing(System.Func<string> key, System.Func<string> version)
        => new CellElement(new ConditionalElement(() => _k.Flash.IsOn(key()),
            new TextElement(() => _k.Flash.Text(key()), Color: () => _k.S.Readable(_k.S.Success(), _k.S.Tile()),
                Width: TrailWidth, Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true },
            _k.Value(version, TrailWidth)), Width: TrailWidth);

    private void Toggle(PluginInfo? p, bool on)
    {
        if (p == null) return;
        _setEnabled(p.Id, on);
        _k.Flash.Flash(p.Id, _loc.T(on ? "common.on" : "common.off"));
        int nOn = 0, total = 0;
        foreach (var q in _inventory.List()) { total++; if (q.IsEnabled && !q.IsErrored) nOn++; }
        _k.Done(_loc.TFormat(on ? "plugins.fb.on" : "plugins.fb.off", NameOf(p), nOn, total));
    }

    private void Retry(PluginInfo? p)
    {
        if (p == null) return;
        _inventory.RequestRetry(p.Id);
        _k.Flash.Flash(p.Id, _loc.T("plugins.flash.started"));
        _k.Done(_loc.TFormat("plugins.fb.started", NameOf(p)));
    }

    // Display only: Id (enable/retry/config) and list order stay on the internal identity.
    private string NameOf(PluginInfo? p)
        => p is null ? "" : DisplayNames?.Resolve(p.Id, p.DisplayName) ?? p.DisplayName;

    private string Initial(PluginInfo? p)
    {
        var n = NameOf(p);
        return n.Length > 0 ? char.ToUpperInvariant(n[0]).ToString() : "";
    }
}
