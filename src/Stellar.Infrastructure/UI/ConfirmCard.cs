using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// The card of a <see cref="ConfirmHostElement"/> (approved CombatMeter-redesign addendum "Modal confirm dialog",
/// 2026-11-17): 340 wide, centred over the scrim. Header = 32 px severity "!" tile + bold title + muted UPPERCASE
/// subtitle; body = one Tile box (muted message + optional bold emphasis line); optional "Don't ask again" toggle; footer
/// = right-aligned Neutral "Cancel" + severity pill, both fixed width. Every text and colour is live (theme × window
/// opacity, language), so one card serves every request of its controller. Built by the WindowBuilder through the
/// factory the host wires (WindowRenderer.ConfirmCardFactory); the scrim, input blocking and Esc live there.
/// </summary>
internal sealed class ConfirmCard
{
    private const float CardWidth = 340f;
    private const float Pad = 14f;
    private const float CardRadius = 10f;
    private const float CancelLabelWidth = 72f;   // the framework "Cancel" in every shipped language (as the reset dialog)
    private const float DefaultConfirmLabelWidth = 96f;
    private const float IconGap = 10f;

    private readonly StellarStyle _s;
    private readonly ILocalization _loc;

    public ConfirmCard(StellarStyle style, ILocalization loc) { _s = style; _loc = loc; }

    public HudElement Build(ConfirmHostElement host)
    {
        var c = host.Controller;
        const float inner = CardWidth - 2f * Pad;
        var column = new ColumnElement(new HudElement[] { Header(c, inner), Body(c, inner), DontAsk(c), Footer(c, host) }, Gap: 10f);
        return new BubbleElement(column)
        {
            FillColorFn = () => _s.Fill(_s.Card()), CornerRadius = CardRadius, PaddingX = Pad, PaddingY = Pad, MaxWidth = CardWidth,
        };
    }

    private static ConfirmSeverity Sev(ConfirmController c) => c.Current?.Severity ?? ConfirmSeverity.Danger;

    private HudElement Header(ConfirmController c, float inner)
    {
        var s = _s;
        var glyph = new TextElement(() => "!", Color: () => Sev(c) == ConfirmSeverity.Caution ? s.CautionLabel() : StellarStyle.White,
            Width: StellarStyle.CardIconSize, Align: TextAlign.Center, NoWrap: true, FontSize: 16) { Bold = true };
        var titleW = inner - StellarStyle.CardIconSize - IconGap;
        return new RowElement(new HudElement[]
        {
            s.FilledTile(glyph, () => TileFill(c), StellarStyle.CardIconSize, 7f),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => c.Current?.Title ?? "", Color: () => s.Text(), Width: titleW, FontSize: 14) { Bold = true },
                new ConditionalElement(() => !string.IsNullOrEmpty(c.Current?.Subtitle),
                    new TextElement(() => (c.Current?.Subtitle ?? "").ToUpperInvariant(), Color: () => s.Muted(),
                        NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true }),
            }, Gap: 1f), Weight: 1f),
        }, Gap: IconGap);
    }

    private ColorRgba TileFill(ConfirmController c) => Sev(c) switch
    {
        ConfirmSeverity.Primary => _s.Primary(),
        ConfirmSeverity.Caution => _s.CautionFill(),
        _ => _s.DangerFill(),
    };

    private HudElement Body(ConfirmController c, float inner)
    {
        var s = _s;
        var w = inner - 20f;   // the box's 10 px padding each side
        return new BubbleElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => c.Current?.Message ?? "", Color: () => s.Muted(), Width: w),
            new ConditionalElement(() => !string.IsNullOrEmpty(c.Current?.Emphasis),
                new TextElement(() => c.Current?.Emphasis ?? "", Color: () => s.Text(), Width: w) { Bold = true }),
        }, Gap: 4f))
        {
            FillColorFn = () => s.Fill(s.Tile()), CornerRadius = StellarStyle.TileRadius, PaddingX = 10f, PaddingY = 8f,
            MaxWidth = inner,
        };
    }

    private HudElement DontAsk(ConfirmController c) => new ConditionalElement(() => c.Current?.DontAskAgainKey != null,
        new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => c.DontAskChecked, v => c.DontAskChecked = v),
            new TextElement(() => _loc.T("confirm.dontAskAgain"), Color: () => _s.Muted(), NoWrap: true),
        }, Gap: 8f));

    private HudElement Footer(ConfirmController c, ConfirmHostElement host)
    {
        var w = host.ConfirmLabelWidth > 0f ? host.ConfirmLabelWidth : DefaultConfirmLabelWidth;
        Func<string> ok = () => c.Current?.ConfirmLabel ?? _loc.T("common.ok");
        // Severity is per request: one pill per kind, the live one picked by a Conditional (PillButton's kind is fixed).
        var confirm = new ConditionalElement(() => Sev(c) == ConfirmSeverity.Danger, _s.DangerButton(ok, c.Confirm, w),
            new ConditionalElement(() => Sev(c) == ConfirmSeverity.Caution, _s.CautionButton(ok, c.Confirm, w),
                _s.PrimaryButton(ok, c.Confirm, w)));
        return new RowElement(new HudElement[]
        {
            new SpacerElement(),   // flexible: right-aligns the pills
            _s.NeutralButton(() => c.Current?.CancelLabel ?? _loc.T("common.cancel"), c.Cancel, CancelLabelWidth),
            confirm,
        }, Gap: 6f);
    }
}
