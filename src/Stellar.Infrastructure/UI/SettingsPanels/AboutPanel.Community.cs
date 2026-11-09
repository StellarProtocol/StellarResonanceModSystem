using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// Settings → About → COMMUNITY (approved 2026-11-09, shown to everyone): [Discord-blurple icon tile] "Questions, bug
// reports and news" [Join Discord]. Brand colour on purpose — the tile and pill are FIXED #5865F2, not theme-tinted
// (still faded by window opacity like every fill). The label is the solid-fill label rule (white passes 3:1 on
// #5865F2; SolidLabel only switches to near-black if a very low window opacity fades the fill too far).
internal sealed partial class AboutPanel
{
    /// <summary>The Stellar community Discord invite (one place to change it).</summary>
    internal const string DiscordInviteUrl = "https://discord.com/invite/8T8gYQhgtu";

    private static readonly ColorRgba Blurple = ColorRgba.FromHex(0x5865F2FF);
    private static readonly ColorRgba HoverLift = StellarStyle.White with { A = 0.10f };
    private const float CommunityTileSize = 26f;
    private const float CommunityTileGlyph = 16f;
    private const float PillGlyph = 16f;

    private HudElement BuildCommunity(SettingsKit k)
    {
        var s = k.S;
        // The glyph art may not be embedded yet: a missing PNG leaves the tile an empty blurple square (no crash).
        var tile = s.FilledTile(new ConditionalElement(() => DiscordIcon() != null,
            new ImageElement(() => DiscordIcon(CommunityTileGlyph), (int)CommunityTileGlyph, (int)CommunityTileGlyph) { Live = true }),
            () => Blurple, CommunityTileSize, 6f);
        // No HelpGap (About has no "?" column): the pill ends flush with the panel padding, under the caption hint.
        return k.Panel("about.community", k.T("about.community.hint"),
            SettingsKit.Row(tile, k.Label(k.T("about.discord.label")), DiscordPill(k)));
    }

    // A 28 px round pill in the brand fill: [16 px discord glyph] "Join Discord". Same shell as StellarStyle's pills
    // (Bubble → Bare Selectable → Row[pad strut, content, pad strut]); without the glyph the pill is text-only.
    private HudElement DiscordPill(SettingsKit k)
    {
        var s = k.S;
        var hover = new StellarStyle.HoverFlag();
        var label = new TextElement(k.T("about.discord.button"), Color: () => s.SolidLabel(Blurple), NoWrap: true,
            FontSize: 13) { Bold = true };
        var glyph = new ConditionalElement(() => DiscordIcon() != null, new RowElement(new HudElement[]
        {
            new ImageElement(() => DiscordIcon(PillGlyph), (int)PillGlyph, (int)PillGlyph) { Live = true },
            new SpacerElement(6f, 1f),
        }, Gap: 0.01f));
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(StellarStyle.PillPadX, StellarStyle.PillHeight),
                glyph,
                StellarStyle.DropBold(label),
                new SpacerElement(StellarStyle.PillPadX, StellarStyle.PillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => OpenDiscord(k))
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => s.Fill(hover.On ? StellarStyle.Over(HoverLift, Blurple) : Blurple),
            CornerRadius = StellarStyle.RoundRadius(StellarStyle.PillHeight), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private void OpenDiscord(SettingsKit k)
    {
        OpenUrl(DiscordInviteUrl);
        k.Done(_loc.T("about.discord.done"));
    }

    private static byte[]? DiscordIcon() => LauncherIcons.Get("discord");

    // The glyph pre-drawn for a <paramref name="glyph"/>-unit image at the live canvas scale (Live images re-pick on a
    // resolution / UI-scale change).
    private static byte[]? DiscordIcon(float glyph) => LauncherIcons.Glyph("discord", glyph);

    private static void OpenInBrowser(string url) => UnityEngine.Application.OpenURL(url);
}
