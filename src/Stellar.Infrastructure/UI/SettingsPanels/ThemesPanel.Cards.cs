using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Theme;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The "Theme" panel: a 2-column grid of 48 px theme cards (built-ins, then custom themes, then "+ Duplicate" — or
// "12 of 12 used" at the limit), 3 rows reserved then it scrolls; under it the active theme's actions (built-in → a
// note; custom → Edit colours / Rename / Delete with an inline rename field and a delete confirm).
internal sealed partial class ThemesPanel
{
    private const int MaxCardSlots = 5 + 24 + 1;   // presets + custom themes (a legacy config may hold more than 12) + the add card
    private const float CardHeight = 48f;
    private const float SwatchSize = 28f;
    private const float CornerSize = 12f;
    private const int CardRows = 3;
    // Left column 293 − panel padding 20 − the scroll viewport's 9 px bar lane = 264; two cells with the list's 2 px gap.
    private const float CardWidth = (LayoutPanel.ColumnWidth - 20f - 9f - 2f) / 2f;

    private bool _confirmDelete;

    private HudElement BuildThemePanel()
    {
        var cards = new HudElement[MaxCardSlots];
        for (var i = 0; i < MaxCardSlots; i++) cards[i] = CardSlot(i);
        var grid = new ScrollElement(new ListElement(() => Math.Min(5 + _store.Names.Count, MaxCardSlots - 1) + 1, cards,
            Columns: 2, CellWidth: CardWidth, CellHeight: CardHeight), Height: CardRows * CardHeight + (CardRows - 1) * 2f)
            { FixedHeight = true };
        return _k.Panel("themes.theme", () => _text.TFormat("themes.customCount", _store.Names.Count, CustomThemeLimit),
            grid, BuildThemeActions());
    }

    private const int CustomThemeLimit = 12;   // == CustomThemeStore.MaxCustomThemes (Application; the store enforces it)

    private bool IsCustomActive => _namedTheme.ActiveCustomName != null;

    private HudElement BuildThemeActions()
    {
        var s = _k.S;
        var confirm = s.ConfirmPanel(new ConfirmSpec(
            () => _text.TFormat("themes.delete.q", _namedTheme.ActiveCustomName ?? ""),
            _k.T("themes.delete.consequence"), _k.T("common.delete"), ConfirmDelete,
            _k.T("common.cancel"), () => _confirmDelete = false));
        var rename = new ColumnElement(new HudElement[]
        {
            SettingsKit.Row(
                new CellElement(new InputElement(() => _editor.NameBuffer, v => { _editor.NameBuffer = v ?? ""; CommitRename(); }, 120f,
                    OnChange: v => _editor.NameBuffer = v ?? "") { SingleLine = true, FocusRequest = () => _renameFocus }, Weight: 1f),
                s.PrimaryButton(_k.T("common.save"), CommitRename),
                s.NeutralButton(_k.T("common.cancel"), _editor.CancelRename)),
            new ConditionalElement(() => _editor.NameError != null,
                new TextElement(() => _editor.NameError ?? "", Color: () => s.Readable(s.Caution(), s.Panel()))),
        }, Gap: 4f);
        var actions = new RowElement(new HudElement[]
        {
            s.PrimaryButton(_k.T("themes.editColours"), OpenColours),
            s.NeutralButton(_k.T("themes.rename"), BeginRename),
            new SpacerElement(),
            s.DangerButton(_k.T("common.delete"), () => _confirmDelete = true),
        }, Gap: 6f);
        var limit = new TextElement(_k.T("themes.limitNote"), Color: () => s.Readable(s.Caution(), s.Panel()));
        return new ConditionalElement(() => !IsCustomActive && !_store.CanCreate, limit,
            new ConditionalElement(() => IsCustomActive && _confirmDelete, confirm,
                new ConditionalElement(() => IsCustomActive && _editor.IsRenaming, rename,
                    new ConditionalElement(() => IsCustomActive, actions, s.MutedLine(_k.T("themes.builtinNote"))))));
    }

    // One grid cell: a preset card (0..4), a custom-theme card, or the trailing add card.
    private HudElement CardSlot(int idx)
    {
        bool IsPreset() => idx < Presets.Length;
        bool IsCustom() => idx >= Presets.Length && idx - Presets.Length < _store.Names.Count;
        string CustomName() => IsCustom() ? _store.Names[idx - Presets.Length] : "";
        ThemePreset BaseOf() => IsPreset() ? Presets[idx] : IsCustom() ? _store.BasePresetOf(CustomName()) : ThemePreset.Default;
        bool Active() => IsPreset() ? _namedTheme.ActiveCustomName == null && _namedTheme.Active == Presets[idx]
                                    : IsCustom() && _namedTheme.ActiveCustomName == CustomName();
        var card = ThemeCard(new CardBinding(
            () => IsPreset() ? _text.T(PresetNameKey(Presets[idx])) : CustomName(),
            () => IsPreset() ? _text.T(SubKey(Presets[idx])) : _text.T("themes.sub.custom"),
            BaseOf, Active, () => SelectCard(idx)));
        return new ConditionalElement(() => IsPreset() || IsCustom(), card, AddCard());
    }

    private readonly record struct CardBinding(Func<string> Name, Func<string> Sub, Func<ThemePreset> Base,
        Func<bool> Active, Action OnClick);

    private HudElement ThemeCard(CardBinding b)
    {
        var s = _k.S;
        var hover = new StellarStyle.HoverFlag();
        // The active theme's swatch reads the LIVE colours (so a custom theme's edits show); others their preset table.
        ColorRgba Bg() => b.Active() ? s.Background() : PresetColor(b.Base(), ThemePresets.MenuBackground);
        ColorRgba Acc() => b.Active() ? s.Accent() : PresetColor(b.Base(), ThemePresets.MenuAccent);
        var row = new RowElement(new HudElement[]
        {
            new SpacerElement(8f, CardHeight),
            Swatch(Bg, Acc),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(b.Name, Color: () => s.Text(), NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true },
                new TextElement(b.Sub, Color: () => s.Muted(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            // "✓" in plain text (bold ✓ is untrusted, SDL §6); an empty string keeps the column when not active.
            new TextElement(() => b.Active() ? "✓" : "", Color: () => s.Readable(s.Accent(), s.AccentWash()), Width: 12f, NoWrap: true),
            new SpacerElement(6f, 1f),
        }, Gap: 8f);
        return new BubbleElement(new SelectableElement(row, OnClick: b.OnClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => s.Fill(b.Active() ? s.AccentWash(s.Tile()) : hover.On ? s.NeutralHover() : s.Tile()),
            OutlineColorFn = () => b.Active() ? s.Accent() : null,
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    // A 28 px rounded square in the theme's window colour with an accent square in its bottom-right corner (the toolkit
    // has no gradient, so the prototype's gradient swatch is its first stop). A colour sample, so it does not fade.
    private static HudElement Swatch(Func<ColorRgba> bg, Func<ColorRgba> accent)
        => new CellElement(new BubbleElement(new ColumnElement(new HudElement[]
        {
            new SpacerElement(SwatchSize, SwatchSize - CornerSize),
            new RowElement(new HudElement[]
            {
                new SpacerElement(SwatchSize - CornerSize, CornerSize),
                new CellElement(new BubbleElement(new SpacerElement(CornerSize, CornerSize))
                {
                    FillColorFn = () => accent() with { A = 1f }, CornerRadius = 4f, PaddingX = 0f, PaddingY = 0f, MaxWidth = CornerSize,
                }, Width: CornerSize),
            }, Gap: 0.01f),
        }, Gap: 0.01f))
        {
            FillColorFn = () => bg() with { A = 1f }, OutlineColorFn = () => new ColorRgba(1f, 1f, 1f, 0.13f),
            CornerRadius = 6f, PaddingX = 0f, PaddingY = 0f, MaxWidth = SwatchSize,
        }, Width: SwatchSize);

    // "+ Duplicate" (a hollow card, hover = Tile) — or, at the 12-theme limit, the italic "12 of 12 used".
    private HudElement AddCard()
    {
        var s = _k.S;
        var hover = new StellarStyle.HoverFlag();
        HudElement Content(HudElement label) => new RowElement(new HudElement[] { new SpacerElement(0f, CardHeight), label },
            Gap: 0.01f, Justify: RowJustify.Center);
        var add = new BubbleElement(new SelectableElement(Content(new TextElement(_k.T("themes.duplicateCard"),
                Color: () => hover.On ? s.Text() : s.Muted(), NoWrap: true, FontSize: 13) { Bold = true }), OnClick: Duplicate)
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? s.Fill(s.Tile()) : StellarStyle.Transparent, OutlineColorFn = () => s.ChipOff(),
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
        var full = new BubbleElement(Content(new TextElement(() => _text.TFormat("themes.limitCard", CustomThemeLimit, CustomThemeLimit),
                Color: () => s.Muted(), NoWrap: true) { Italic = true }))
        {
            FillColorFn = () => StellarStyle.Transparent, OutlineColorFn = () => s.ChipOff(),
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
        return new ConditionalElement(() => _store.CanCreate, add, full);
    }

    private static ColorRgba PresetColor(ThemePreset p, int index)
        => ThemePresets.Tables.TryGetValue(p, out var t) && index < t.Length ? t[index] : new ColorRgba(0.5f, 0.5f, 0.5f, 1f);

    private static string SubKey(ThemePreset p) => p switch
    {
        ThemePreset.Dark    => "themes.sub.dark",
        ThemePreset.Light   => "themes.sub.light",
        ThemePreset.Crimson => "themes.sub.crimson",
        ThemePreset.Sakura  => "themes.sub.sakura",
        _                   => "themes.sub.default",
    };
}
