using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

// The compact bar (Minimal mode, prototype .compact): ✕ + logo · Full-menu / rotate buttons · the favorite tiles (or
// the empty hint) · Settings. Vertical = one column; horizontal = one row (the same blocks, side by side).
internal sealed partial class LauncherView
{
    private const int MaxFavorites = 16;        // compact-bar tile pool (unchanged capacity)
    private const float CompactTileWidth = 56f;
    private const float CompactTileHeight = 68f;
    private const float CompactTileTop = 6f;
    private const float CompactNameGap = 3f;
    // Name slot 26 (prototype 24) + 1 below (prototype 3): same 68 px tile, but two TMP lines can't outgrow the slot
    // (see NameSlot in .Tile.cs).
    private const float CompactNameSlot = 26f;
    private const int CompactNameFont = 10;     // prototype 10.5 (FontSize is whole pixels)
    private const float CompactLogo = 24f;
    private const float CompactLogoCell = 32f;
    private const int CompactHintFont = 11;

    private HudElement BuildCompact(bool horizontal)
    {
        HudElement Pair(HudElement a, HudElement b) => horizontal
            ? new RowElement(new[] { a, b }, Gap: 2f)
            : new ColumnElement(new[] { Centered(a), Centered(b) }, Gap: 2f);
        HudElement Sep() => new SeparatorElement(Vertical: horizontal);

        var logoCell = new RowElement(new HudElement[]
        {
            new SpacerElement((CompactLogoCell - CompactLogo) / 2f, CompactLogoCell),
            new BrandLogoElement(() => Icon("stellar"), () => Icon("stellar-glow"), Size: CompactLogo),
            new SpacerElement((CompactLogoCell - CompactLogo) / 2f, CompactLogoCell),
        }, Gap: 0.01f);

        var items = new List<HudElement>(MaxFavorites + 8)
        {
            Pair(_s.CloseIconButton(_actions.Close, () => _s.Background()), logoCell),
            Sep(),
            Pair(_s.IconButton(() => Icon("mode_grid"), () => SwitchMode(LauncherMode.Full)),
                 _s.IconButton(() => Icon("rotate"), () => _registry.MinimalHorizontal = !_registry.MinimalHorizontal)),
            Sep(),
        };
        for (var i = 0; i < MaxFavorites; i++)
        {
            var idx = i;
            items.Add(new ConditionalElement(() => idx < _model.FavoriteCount, CompactFavoriteTile(idx)));
        }
        items.Add(new ConditionalElement(() => _model.FavoriteCount == 0, new CellElement(
            Small(() => T("launcher.compact.empty"), () => _s.Muted(), CompactHintFont, TextAlign.Center,
                maxLines: horizontal ? 3 : 8),
            Width: horizontal ? 140f : CompactTileWidth)));
        items.Add(Sep());
        items.Add(CompactSettingsTile());

        return horizontal ? new RowElement(items, Gap: 6f) : new ColumnElement(items, Gap: 6f);
    }

    private HudElement CompactFavoriteTile(int index)
    {
        LauncherEntry? Entry() => _model.FavoriteAt(index);
        var icon = LiveIconTile(() => EntryIcon(Entry()), () => _s.Fill(_s.Accent()), () => StellarStyle.White, () => 1f);
        return CompactTileShell(icon, () => Entry() is { } e ? LauncherMenuModel.NameOf(e) : "",
            () => Open(Entry()), () => Flashing(Entry()));
    }

    // The Settings entry at the end of the bar: a Neutral icon tile with the settings glyph in the neutral label colour.
    private HudElement CompactSettingsTile()
    {
        var icon = LiveIconTile(() => Icon("settings"), () => _s.Fill(_s.Neutral()), () => _s.NeutralLabel(), () => 1f);
        return CompactTileShell(icon, () => T("launcher.settings"), () => _actions.OpenSettings(null), () => false);
    }

    // 56 × 68 (prototype .ctile): transparent at rest, Tile fill + text-coloured name on hover, Success wash on the
    // click flash; 32 px icon tile at the top, the name in a fixed two-line slot (10 px, muted).
    private HudElement CompactTileShell(HudElement iconTile, Func<string> label, Action onClick, Func<bool> flashing)
    {
        var hover = new StellarStyle.HoverFlag();
        var nameRow = new RowElement(new HudElement[]
        {
            new SpacerElement(2f, CompactNameSlot),
            new CellElement(Small(label, () => hover.On ? _s.Text() : _s.Muted(), CompactNameFont, TextAlign.Center,
                maxLines: 2), Weight: 1f),
            new SpacerElement(2f, 1f),
        }, Gap: 0.01f);
        var body = new ColumnElement(new HudElement[]
        {
            new SpacerElement(0f, CompactTileTop),
            Centered(iconTile),
            new SpacerElement(0f, CompactNameGap),
            nameRow,
            new SpacerElement(0f, CompactTileHeight - CompactTileTop - TileIcon - CompactNameGap - CompactNameSlot),
        }, Gap: 0.01f);
        return new CellElement(new BubbleElement(new SelectableElement(body, OnClick: onClick)
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => flashing() ? _s.Fill(StellarStyle.Over(_s.Success() with { A = 0.25f }, _s.Card()))
                              : hover.On ? _s.Fill(_s.Tile()) : StellarStyle.Transparent,
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f, MaxWidth = CompactTileWidth,
        }, Width: CompactTileWidth);
    }
}
