using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

// The Full-menu plugin tile (prototype .tile, icon enlarged 2026-11-09): a Card bubble, 90 × 102, with a 44 px accent
// icon tile at a fixed spot, the name in a hard-fixed two-line slot and the favorite star in the top-right corner. Every
// part has a fixed height, so every tile is exactly the same size whatever its name. Pooled: slot i shows the model's
// i-th tile of the current tab, so every Func reads the entry live.
internal sealed partial class LauncherView
{
    // Launcher icon tile: 44 px accent tile, the glyph EXACTLY 32 px (user request: the old launcher's glyphs were ~30 px,
    // the 62 % kit glyph read too small). 32 is an exact 2×/4×/8× downscale of the 64/128/256 px plugin PNGs at 1 unit =
    // 1 px (2560×1440), so it lands on a mip level; 30 blurred. (44 − 32) / 2 = 6 px padding — whole pixels, no half-pixel
    // offset. Launcher-only: the kit's IconTile stays 32 / 62 %.
    private const float TileIcon = 44f;
    private const int TileGlyph = 32;
    private const float TileIconTop = 10f;        // prototype padding-top 10: the icon sits at the same y in every tile
    private const float StarSize = 20f;           // round star toggle, 3 px from the top-right corner
    private const float StarInset = 3f;
    private const int StarGlyph = 14;             // even, so the glyph sits whole-pixel centred (3 px each side) in the slot
    private const float NameGap = 4f;
    // The name slot is a HARD 38 px (TextElement.Height): two 12 px TMP lines measure ~37 (in-game 2026-11-09, a 32 px
    // strut slot grew wrapped-name tiles to 88–89 while one-line tiles stayed 84, so the icons in a row sat at different
    // heights). The pinned box never grows its row and a one-line name centres in it. 6 px below it.
    private const float NameSlot = 38f;
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

    // The 20 px round star (prototype .pin): a 14 px muted star while the tile is hovered, filled accent star whenever the
    // plugin is a favorite; invisible otherwise (alpha 0, so nothing moves). Its own click toggles the favorite and
    // never opens the plugin (the inner Selectable takes the click; the tile's is its parent).
    // The outline was Muted at 13 px and nearly invisible on the hovered card (user report 2026-11-09): it is now Text at
    // 70 % composited over the HOVERED tile fill and floored to 3:1 against it (holds for Light / Sakura too); with the
    // pointer on the star itself it goes full Text on the accent-wash bubble, so it reads as a button.
    // The thin OUTLINE glyph was still hard to notice (user, 2026-11-09): the un-favorited star is now the same FILLED
    // glyph in muted grey (3:1 against the hovered tile) — the shape reads; colour alone says favorited (accent) or not.
    private HudElement StarToggle(Func<LauncherEntry?> entry, StellarStyle.HoverFlag tileHover, Func<float> alpha)
    {
        var hover = new StellarStyle.HoverFlag();
        bool Fav() => entry() is { } e && _model.IsFavorite(e);
        ColorRgba Wash() => _s.AccentWash(_s.NeutralHover());   // the star's own hover bubble, over the hovered tile
        ColorRgba Tint()
        {
            ColorRgba c;
            if (Fav()) c = StellarStyle.EnsureContrast(_s.Accent(), _s.Card(), StellarStyle.SolidMinContrast);
            else if (hover.On) c = StellarStyle.EnsureContrast(_s.Text(), Wash(), StellarStyle.SolidMinContrast);
            else if (tileHover.On)
            {
                c = StellarStyle.EnsureContrast(_s.Muted(), _s.NeutralHover(), StellarStyle.SolidMinContrast);
            }
            else c = StellarStyle.Transparent;
            return Faded(c, alpha());
        }
        const float pad = (StarSize - StarGlyph) / 2f;
        return new CellElement(new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(pad, StarSize),
                Glyph(() => Icon("star"), StarGlyph, Tint),
                new SpacerElement(pad, StarSize),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => ToggleFavorite(entry())) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => hover.On ? Faded(_s.Fill(Wash()), alpha()) : StellarStyle.Transparent,
            CornerRadius = StellarStyle.RoundRadius(StarSize), PaddingX = 0f, PaddingY = 0f, MaxWidth = StarSize,
        }, Width: StarSize);
    }

    // The kit's accent IconTile look (SDL "Icon tile", radius 22 % of the side), but LIVE — a pooled tile shows a
    // different plugin's icon after a tab / Find change — fadable with its tile, and at the launcher's own size: a 44 px
    // tile (radius 10) around an exactly 32 px glyph, 6 px padding (see TileIcon).
    private static HudElement LiveIconTile(Func<byte[]?> png, Func<ColorRgba> fill, Func<ColorRgba> glyphTint,
        Func<float> alpha)
    {
        const float size = TileIcon;
        const int glyph = TileGlyph;
        const float pad = (size - glyph) / 2f;   // 6: a whole pixel, so the glyph sits on the pixel grid
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

    // The fixed two-line name slot: left/right insets, the slot-height strut, the centred MaxLines-2 name in a box pinned
    // to the slot height (so a TMP line box taller than expected can't grow the tile).
    private HudElement GridTileNameRow(Func<LauncherEntry?> entry, Func<float> alpha)
        => new RowElement(new HudElement[]
        {
            new SpacerElement(NameInset, NameSlot),   // left inset + the fixed slot height (a one-line name centres in it)
            new CellElement(Small(() => entry() is { } e ? LauncherMenuModel.NameOf(e) : "",
                () => Faded(_s.Text(), alpha()), NameFont, TextAlign.Center, maxLines: 2) with { Height = NameSlot }, Weight: 1f),   // hard-fixed box: never grows the tile
            new SpacerElement(NameInset, 1f),
        }, Gap: 0.01f);
}
