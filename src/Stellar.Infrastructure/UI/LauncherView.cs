using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Declarative (uGUI) launcher — wired to <see cref="LauncherRegistry"/>/<see cref="LauncherPrefs"/>. ONE GlassMenu frame with NO
/// chrome title bar (<see cref="WindowSpec.ShowTitleBar"/> = false); the launcher self-draws its header inside
/// the body so the controls can sit on TOP (Full / Minimal-vertical) or in a LEFT strip (Minimal-horizontal),
/// matching the pinned mockup. The body switches via live Conditionals on the registry mode; content-sized width
/// tracks the active mode. Settings collapses to one ⚙ entry that opens the multi-tab uGUI Settings hub.
/// </summary>
/// <remarks>
/// Built ONCE; structural changes (mode, pin) are Conditionals re-evaluated each apply (a visibility change
/// forces a layout rebuild so the content-sized window re-fits). Each Minimal plugin tile is
/// <c>Conditional(IsPinned)</c> so pinning in Full reflects live without a rebuild.
/// </remarks>
internal sealed class LauncherView
{
    private const int Cols = 4;
    private const int MaxRows = 8;          // Full grid pool: 8×4 = 32 plugin tiles
    private const int MaxPinned = 16;       // Minimal pool: up to 16 pinned tiles
    private const float CloseWidth = 24f;   // ✕ tile width (also the left balancer width for true logo centring)
    private static readonly ColorRgba SectionMuted = new(0.52f, 0.58f, 0.66f, 0.9f);

    private readonly LauncherRegistry _registry;
    private readonly Action _openSettings;
    private readonly Action _close;

    // LIVE plugin lists, rebuilt only when the registry revision changes (plugin register/unregister or a pin
    // toggle bump it). The tile pools below read these by index so plugins that register AFTER the launcher is
    // built (most do — they load asynchronously) still appear, and the pinned count stays consistent with the
    // visible tiles (the build-time snapshot was why a late-pinned plugin left a stray separator + no tile).
    private readonly List<LauncherEntry> _all = new();
    private readonly List<LauncherEntry> _pinned = new();
    private int _rev = -1;

    public LauncherView(LauncherRegistry registry, Action openSettings, Action close)
    { _registry = registry; _openSettings = openSettings; _close = close; }

    private void Refresh()
    {
        if (_rev == _registry.Revision) return;
        _rev = _registry.Revision;
        _all.Clear(); _pinned.Clear();
        foreach (var e in _registry.Entries)
            if (e.Group == LauncherGroup.Plugin) { _all.Add(e); if (_registry.IsPinned(e)) _pinned.Add(e); }
    }
    private int AllCount() { Refresh(); return _all.Count; }
    private int PinnedCount() { Refresh(); return _pinned.Count; }

    private static byte[]? Icon(string name) => LauncherIcons.Get(name);
    private bool Full => _registry.Mode == LauncherMode.Full;
    private bool Minimal => _registry.Mode == LauncherMode.Minimal;

    /// <summary>Body: the three mode layouts (each with its own header), only the active one shown.</summary>
    public HudElement Root => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => Full, BuildFull()),
        new ConditionalElement(() => Minimal && !_registry.MinimalHorizontal, BuildMinimalVertical()),
        new ConditionalElement(() => Minimal && _registry.MinimalHorizontal, BuildMinimalHorizontal()),
    });

    // ── header pieces (each call builds a fresh element; one set per mode body) ──
    private HudElement Logo => new BrandLogoElement(() => Icon("stellar"), () => Icon("stellar-glow"), Size: 22f);
    private HudElement ModeToggle => new TileElement(
        () => Icon(Full ? "mode_list" : "mode_grid"), null,   // icon-only
        () => _registry.Mode = Full ? LauncherMode.Minimal : LauncherMode.Full, Width: 26f, IconSize: 16f);
    private HudElement RotateToggle => new TileElement(
        () => Icon("rotate"), null, () => _registry.MinimalHorizontal = !_registry.MinimalHorizontal,   // icon-only
        Width: 26f, IconSize: 16f);
    private HudElement CloseBtn => new TileElement(() => null, () => "✕", _close, Width: CloseWidth, IconSize: 14f);

    private HudElement SettingsTile(float width) => new TileElement(
        () => Icon("settings"), () => "Settings", _openSettings, Width: width);

    // A live plugin tile reading the idx-th entry of the all/pinned list each apply (null when out of range).
    private HudElement LivePluginTile(int idx, bool pinnedOnly)
    {
        LauncherEntry? At() { Refresh(); var list = pinnedOnly ? _pinned : _all; return idx < list.Count ? list[idx] : null; }
        return new TileElement(
            () => At()?.IconPng ?? Icon("plugins"), () => At()?.Title ?? "",
            () => { var e = At(); if (e != null) SafeOpen(e); },
            Pinned: () => { var e = At(); return e != null && _registry.IsPinned(e); },
            OnTogglePin: () => { var e = At(); if (e != null) _registry.SetPinned(e, !_registry.IsPinned(e)); });
    }

    // Full: top header, a "PLUGINS" label + grid (only when plugins exist), a separator, then ⚙ Settings. The
    // grid is a fixed pool of 8 rows × 4 live tiles; each row/tile shows only while its index < the live count.
    private HudElement BuildFull()
    {
        var body = new List<HudElement>
        {
            new RowElement(new HudElement[] { Logo, new TextElement(() => "Stellar", null, Emphasis: true), new SpacerElement(), ModeToggle, CloseBtn }, Gap: 6f),
            new ConditionalElement(() => AllCount() > 0, new TextElement(() => "PLUGINS", () => SectionMuted, Emphasis: true)),
        };
        for (var r = 0; r < MaxRows; r++)
        {
            var start = r * Cols;
            var rowTiles = new HudElement[Cols];
            for (var c = 0; c < Cols; c++) { var i = start + c; rowTiles[c] = new ConditionalElement(() => i < AllCount(), LivePluginTile(i, false)); }
            body.Add(new ConditionalElement(() => start < AllCount(), new RowElement(rowTiles, Gap: 12f)));
        }
        body.Add(new ConditionalElement(() => AllCount() > 0, new SeparatorElement()));
        body.Add(SettingsTile(120f));
        return new ColumnElement(body.ToArray(), Gap: 10f);
    }

    // Minimal vertical (per the user's layout spec):
    //   [ logo · ✕ ] ─── [ expand · rotate ] ─[only if pinned]─ [ pinned tiles ] ─── [ ⚙ Settings ]
    private HudElement BuildMinimalVertical()
    {
        var items = new List<HudElement>
        {
            // logo TRULY centred: fixed left spacer = ✕ width balances the right-aligned ✕ (Gap 0 = exact centre).
            new RowElement(new HudElement[] { new SpacerElement(CloseWidth), new SpacerElement(), Logo, new SpacerElement(), CloseBtn }, Gap: 0f),
            new SeparatorElement(),
            new RowElement(new HudElement[] { new SpacerElement(), ModeToggle, RotateToggle, new SpacerElement() }, Gap: 8f),
            // The plugins rule shows only when a pinned tile actually renders (count>0) — no stray double line.
            new ConditionalElement(() => PinnedCount() > 0, new SeparatorElement()),
        };
        for (var i = 0; i < MaxPinned; i++) { var idx = i; items.Add(new ConditionalElement(() => idx < PinnedCount(), LivePluginTile(idx, true))); }
        items.Add(new SeparatorElement());
        items.Add(SettingsTile(88f));
        return new ColumnElement(items.ToArray(), Gap: 6f);
    }

    // Minimal horizontal (per the user's layout spec):
    //   [ ✕ / logo ] | [ expand / rotate ] | [ pinned tiles ] |[only if pinned] [ ⚙ Settings ]
    private HudElement BuildMinimalHorizontal()
    {
        var chromePane = new ColumnElement(new HudElement[] { CloseBtn, Logo }, Gap: 4f);
        var togglePane = new ColumnElement(new HudElement[] { ModeToggle, RotateToggle }, Gap: 4f);
        var tiles = new HudElement[MaxPinned];
        for (var i = 0; i < MaxPinned; i++) { var idx = i; tiles[i] = new ConditionalElement(() => idx < PinnedCount(), LivePluginTile(idx, true)); }
        return new RowElement(new HudElement[]
        {
            chromePane, new SeparatorElement(Vertical: true),
            togglePane, new SeparatorElement(Vertical: true),
            new RowElement(tiles, Gap: 8f),
            new ConditionalElement(() => PinnedCount() > 0, new SeparatorElement(Vertical: true)),
            SettingsTile(88f),
        }, Gap: 8f);
    }

    private static void SafeOpen(LauncherEntry e) { try { e.OnOpen(); } catch { /* never break the launcher */ } }
}
