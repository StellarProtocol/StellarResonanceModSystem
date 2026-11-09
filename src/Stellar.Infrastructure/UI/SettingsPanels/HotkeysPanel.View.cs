using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The house-style view of Settings → Hotkeys: "Game input" (the don't-pass-through switch) over "Hotkeys" — filter
// chips All / Framework / Plugins + Reset all (confirm), then the grouped list: collapsible group headers
// [chevron, bold plugin name, muted count] and indented action rows [name, key pill, ✕]. The key pill: bound = bold
// combination, unbound = muted italic "Not set", capturing = accent wash + accent ring "Press a key…".
internal sealed partial class HotkeysPanel
{
    private const float FilterChipWidth = 80f;
    private const float KeyPillWidth = 120f;   // ≥ the prototype's 110 min-width; long combinations ellipsize
    private const float KeyPillHeight = 24f;
    private const float FlashWidth = 62f;
    private const float RowIndent = 22f;       // + the tile's 6 px inset = the prototype's 28 px indent

    private SettingsKit? _k;
    private bool _confirmReset;

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        kit.TabChanged += () => { _confirmReset = false; if (_capturingActionId != null) CancelCapture(); };
        var s = kit.S;
        var slots = new HudElement[PoolRows];
        for (var i = 0; i < PoolRows; i++) slots[i] = StellarStyle.ListSlot(BuildHotkeySlot(i));
        var confirm = s.ConfirmPanel(new ConfirmSpec(kit.T("hotkeys.reset.q"), kit.T("hotkeys.reset.consequence"),
            kit.T("common.resetAll"), ConfirmResetAll, kit.T("common.cancel"), () => _confirmReset = false) { Width = SettingsHub.MainWidth - 20f });
        var chips = new RowElement(new HudElement[]
        {
            FilterChip("hotkeys.filter.all", Filter.All),
            FilterChip("hotkeys.filter.framework", Filter.Framework),
            FilterChip("hotkeys.filter.plugins", Filter.Plugins),
            new SpacerElement(),
            new ConditionalElement(() => !_confirmReset, s.NeutralButton(kit.T("common.resetAll"), () => _confirmReset = true, 116f)),
        }, Gap: 6f);
        var list = new ConditionalElement(
            () => { RebuildDisplay(); return _display.Count > 0; },
            // Virtual: no row cap (the old 64-row pool dropped everything after row 64). Its viewport is flexible
            // (LayoutElement flexibleHeight 1), so like the old ScrollElement it takes the panel's slack down to the
            // footer and shrinks while the Reset-all confirm is open. Pitch = the 26 px tile + 2 px gap (ListSlot).
            new VirtualListElement(() => _display.Count, StellarStyle.ListRowPitch, slots, first => _first = first,
                Height: 120f),
            s.MutedLine(kit.T("hotkeys.none")));
        return new ColumnElement(new HudElement[]
        {
            kit.Panel("hotkeys.gameInput", null,
                SettingsKit.Row(kit.Label(kit.T("hotkeys.passthrough.label")),
                    SettingsKit.Toggle(() => _blockDirectory.GetBlockAllFromGame(), v => _blockDirectory.SetBlockAllFromGame(v)),
                    kit.Help("hotkeys.passthrough", "hotkeys.passthrough.label", "help.hotkeys.passthrough", "tab.hotkeys"))),
            kit.Panel("tab.hotkeys", null, chips, new ConditionalElement(() => _confirmReset, confirm), list),
        }, Gap: 8f);
    }

    /// <summary>Header status: "4 bound · 7 unbound".</summary>
    public string Status(SettingsKit k)
    {
        int bound = 0, total = 0;
        foreach (var a in _directory.Actions) { total++; if (a.CurrentBinding != null) bound++; }
        return _loc.TFormat("hotkeys.status", bound, total - bound);
    }

    private HudElement FilterChip(string key, Filter f)
        => _k!.S.Chip(_k.T(key), () => _filter == f, () => _filter = f, FilterChipWidth, _k.S.Accent);

    // Pool slot → its CURRENT logical row (_first + slot): every row Func resolves through it, so a slot shows
    // whatever item the scroll window puts under it.
    private HudElement BuildHotkeySlot(int slot)
    {
        int Idx() => _first + slot;
        bool IsHeader() => Idx() < _display.Count && _display[Idx()].IsHeader;
        return new ConditionalElement(IsHeader, HeaderRow(Idx), ActionRow(Idx));
    }

    // A group header: a transparent row (Tile on hover) — chevron, bold name, muted count. Click toggles the group.
    private HudElement HeaderRow(Func<int> idx)
    {
        var s = _k!.S;
        HkRow Row() => idx() < _display.Count ? _display[idx()] : default;
        var hover = new StellarStyle.HoverFlag();
        var row = StellarStyle.TileRow(
            // "▼"/"▶" in PLAIN text draw today (the bold TMP face lacks "▾"; see SDL §6) — one element, text swaps.
            new TextElement(() => _collapsed.Contains(Row().GroupKey) ? "▶" : "▼", Color: () => s.Readable(s.Accent(), s.Panel()),
                Width: 16f, Align: TextAlign.Center, NoWrap: true),
            new CellElement(new TextElement(() => HeaderText(Row()), Color: () => s.Text(), NoWrap: true)
                { Bold = true, Ellipsize = true }, Weight: 1f),
            new TextElement(() => Row().Count.ToString(), Color: () => s.Muted(), NoWrap: true));
        return new BubbleElement(new SelectableElement(row, OnClick: () => ToggleGroup(Row().GroupKey))
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? s.Fill(s.Tile()) : StellarStyle.Transparent,
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement ActionRow(Func<int> idx)
    {
        var s = _k!.S;
        IHotkeyAction? A() => idx() < _display.Count ? _display[idx()].Action : null;
        string Id() => A()?.Id ?? "";
        bool Capturing() => A() is { } a && _capturingActionId == a.Id;
        bool Bound() => A()?.CurrentBinding != null;
        var xHover = new StellarStyle.HoverFlag();
        var row = StellarStyle.TileRow(
            new SpacerElement(RowIndent - 8f, 1f),
            _k.LineLabel(() => RowLabel(A())),
            new ConditionalElement(() => _k.Flash.IsOn(Id()),
                new TextElement(() => _k.Flash.Text(Id()), Color: () => s.Readable(s.Success(), s.Tile()), Width: FlashWidth,
                    Align: TextAlign.Right, NoWrap: true, FontSize: 12) { Bold = true }),
            KeyPill(A, Capturing, Bound),
            new ConditionalElement(() => Bound() && !Capturing(),
                s.SmallCloseButton(() => { if (A() is { } a) Unbind(a.Id); }, hover: xHover),
                SettingsKit.HelpGap()));
        return s.ListTile(row, flashing: () => _k.Flash.IsOn(Id()));
    }

    // Three complete pills (weight / style can't change live): capturing, bound, unbound — sharing one hover flag.
    private HudElement KeyPill(Func<IHotkeyAction?> a, Func<bool> capturing, Func<bool> bound)
    {
        var s = _k!.S;
        var hover = new StellarStyle.HoverFlag();
        void Click() { if (a() is { } act) ToggleCapture(act.Id); }
        HudElement Pill(HudElement label, Func<ColorRgba> fill, bool ring) => new CellElement(new BubbleElement(
            new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, KeyPillHeight), label,
            }, Gap: 0.01f, Justify: RowJustify.Center), OnClick: Click) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => s.Fill(hover.On ? s.Hover(fill()) : fill()),
            OutlineColorFn = ring ? (Func<ColorRgba?>)(() => s.Accent()) : null,
            CornerRadius = StellarStyle.RoundRadius(KeyPillHeight), PaddingX = 10f, PaddingY = 0f, MaxWidth = KeyPillWidth,
        }, Width: KeyPillWidth);
        HudElement Text(Func<string> t, Func<ColorRgba?> c, bool bold, bool italic) => new TextElement(t, Color: c,
            Width: KeyPillWidth - 20f, Align: TextAlign.Center, NoWrap: true, FontSize: 12) { Bold = bold, Italic = italic, Ellipsize = true };
        var capturingPill = Pill(Text(_k.T("hotkeys.pressKey"), () => s.Readable(s.Accent(), s.AccentWash()), true, false),
            () => s.AccentWash(s.Tile()), true);
        var boundPill = Pill(Text(() => a() is { } act ? GetOrBuildBindingLabel(act) : "", () => s.NeutralLabel(), true, false),
            s.Neutral, false);
        var unboundPill = Pill(Text(_k.T("hotkeys.notSet"), () => s.Muted(), false, true), s.Neutral, false);
        return new ConditionalElement(capturing, capturingPill, new ConditionalElement(bound, boundPill, unboundPill));
    }

    private void ConfirmResetAll()
    {
        _confirmReset = false;
        ResetAllToDefaults();
        _k?.Done(_loc.T("hotkeys.fb.resetAll"));
    }

    // Row flash ("Saved" / "Cleared") + footer line after a key change. Only reachable from a click (or a capture the
    // player started by clicking) in the shown hub, so reading the language here is safe.
    private void Confirm(string actionId, string flashKey, string feedbackKey, string? binding)
    {
        if (_k == null) return;
        _k.Flash.Flash(actionId, _loc.T(flashKey));
        string name = "";
        foreach (var a in _directory.Actions) if (a.Id == actionId) { name = RowLabel(a); break; }
        _k.Done(binding == null ? _loc.TFormat(feedbackKey, name) : _loc.TFormat(feedbackKey, name, binding));
    }
}
