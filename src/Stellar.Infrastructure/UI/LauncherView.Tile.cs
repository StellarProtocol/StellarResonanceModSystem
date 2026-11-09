using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

// The Full-menu plugin tile (prototype .tile): a Card bubble, 82 × 84, with a 32 px accent icon tile at a fixed spot,
// the name in a fixed two-line slot and the favorite star in the top-right corner. Pooled: slot i shows the model's
// i-th tile of the current tab, so every Func reads the entry live.
internal sealed partial class LauncherView
{
    private const float TileIcon = 32f;           // = StellarStyle.CardIconSize
    private const float TileIconTop = 10f;        // prototype padding-top 10: the icon sits at the same y in every tile
    private const float StarSize = 20f;           // round star toggle, 3 px from the top-right corner
    private const float StarInset = 3f;
    private const int StarGlyph = 13;
    private const float NameGap = 5f;
    // The name slot is 32 tall (prototype 28) with 5 px below (prototype 9) — same 84 px tile. TMP's line box for two
    // 12 px lines can exceed 28 px; a slot the text can outgrow would make that tile taller than its neighbours and
    // shift the row. 32 keeps every tile exactly 84 whatever the face's line height.
    private const float NameSlot = 32f;
    private const float NameInset = 4f;
    private const int NameFont = 12;
    private const float GhostAlpha = 0.45f;       // un-starred tile kept in Favorites until the tab changes

    private HudElement BuildGridTile(int index)
    {
        LauncherEntry? Entry() => _model.ShownAt(index);
        float Alpha() { var e = Entry(); return e != null && _model.IsGhost(e) ? GhostAlpha : 1f; }
        var hover = new StellarStyle.HoverFlag();

        ColorRgba Fill()
        {
            var e = Entry();
            var c = Flashing(e) ? StellarStyle.Over(_s.Success() with { A = 0.25f }, _s.Card())
                  : hover.On ? _s.NeutralHover() : _s.Card();
            return Faded(_s.Fill(c), Alpha());
        }

        var topRow = GridTileTopRow(Entry, hover, Alpha);
        var nameRow = GridTileNameRow(Entry, Alpha);

        var body = new ColumnElement(new HudElement[]
        {
            new SpacerElement(0f, StarInset),
            topRow,
            new SpacerElement(0f, NameGap),
            nameRow,
            new SpacerElement(0f, TileHeight - StarInset - (TileIconTop - StarInset + TileIcon) - NameGap - NameSlot),
        }, Gap: 0.01f);

        return new CellElement(new BubbleElement(new SelectableElement(body, OnClick: () => Open(Entry()))
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fill(), CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
            MaxWidth = TileWidth,
        }, Width: TileWidth);
    }

    // The 20 px round star (prototype .pin): outline in muted while the tile is hovered, filled accent star whenever the
    // plugin is a favorite; invisible otherwise (alpha 0, so nothing moves). Its own click toggles the favorite and
    // never opens the plugin (the inner Selectable takes the click; the tile's is its parent).
    private HudElement StarToggle(Func<LauncherEntry?> entry, StellarStyle.HoverFlag tileHover, Func<float> alpha)
    {
        var hover = new StellarStyle.HoverFlag();
        bool Fav() => entry() is { } e && _model.IsFavorite(e);
        ColorRgba Tint()
        {
            var c = Fav() ? StellarStyle.EnsureContrast(_s.Accent(), _s.Card(), StellarStyle.SolidMinContrast)
                  : tileHover.On ? _s.Muted() : StellarStyle.Transparent;
            return Faded(c, alpha());
        }
        const float pad = (StarSize - StarGlyph) / 2f;
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(pad, StarSize),
                Glyph(() => Icon(Fav() ? "star" : "star_outline"), StarGlyph, Tint, live: true),
                new SpacerElement(pad, StarSize),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => ToggleFavorite(entry())) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? Faded(_s.Fill(_s.AccentWash(_s.Card())), alpha()) : StellarStyle.Transparent,
            CornerRadius = StellarStyle.RoundRadius(StarSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = StarSize,
        }, Width: StarSize);
    }

    // The kit's accent IconTile (SDL "Icon tile": 32 px, radius 7, white glyph at 62 %), but LIVE — a pooled tile shows
    // a different plugin's icon after a tab / Find change — and fadable with its tile.
    private static HudElement LiveIconTile(Func<byte[]?> png, Func<ColorRgba> fill, Func<ColorRgba> glyphTint,
        Func<float> alpha, float size = TileIcon)
    {
        var glyph = (int)MathF.Round(size * 0.62f);
        var pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                Glyph(png, glyph, () => Faded(glyphTint(), alpha()), live: true),
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Faded(fill(), alpha()), CornerRadius = MathF.Round(size * 0.22f),
            PaddingX = pad, PaddingY = pad, MaxWidth = size,
        }, Width: size);
    }

    // Star-inset strut, then [balancer, flex, icon (7 px lower), flex, star, inset]: the icon is centred and sits
    // 10 px from the top of every tile; the star sits 3 px from the top-right corner.
    private HudElement GridTileTopRow(Func<LauncherEntry?> entry, StellarStyle.HoverFlag hover, Func<float> alpha)
        => new RowElement(new HudElement[]
        {
            new SpacerElement(StarSize + StarInset, 1f),   // balances the star so the icon is truly centred
            new SpacerElement(0f),
            new ColumnElement(new HudElement[]
            {
                new SpacerElement(0f, TileIconTop - StarInset),
                LiveIconTile(() => EntryIcon(entry()), () => _s.Fill(_s.Accent()), () => StellarStyle.White, alpha),
            }, Gap: 0.01f),
            new SpacerElement(0f),
            new ColumnElement(new HudElement[]
            {
                StarToggle(entry, hover, alpha),
                new SpacerElement(0f, TileIconTop - StarInset + TileIcon - StarSize),   // star at the top of the row
            }, Gap: 0.01f),
            new SpacerElement(StarInset, 1f),
        }, Gap: 0.01f);

    // The fixed two-line name slot: left/right insets, the slot-height strut, the centred MaxLines-2 name.
    private HudElement GridTileNameRow(Func<LauncherEntry?> entry, Func<float> alpha)
        => new RowElement(new HudElement[]
        {
            new SpacerElement(NameInset, NameSlot),   // left inset + the fixed slot height (a one-line name centres in it)
            new CellElement(Small(() => entry() is { } e ? LauncherMenuModel.NameOf(e) : "",
                () => Faded(_s.Text(), alpha()), NameFont, TextAlign.Center, maxLines: 2), Weight: 1f),
            new SpacerElement(NameInset, 1f),
        }, Gap: 0.01f);
}
