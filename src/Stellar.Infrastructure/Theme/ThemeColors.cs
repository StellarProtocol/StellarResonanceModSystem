using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Theme;

/// <summary>Phase 8 Default theme palette. Hex constants match the spec.</summary>
internal sealed class ThemeColors : IThemeColors
{
    public ColorRgba Accent      { get; } = ColorRgba.FromHex(0x5fe8c5ffu);
    public ColorRgba Gold        { get; } = ColorRgba.FromHex(0xe8c84affu);
    public ColorRgba HpFill      { get; } = ColorRgba.FromHex(0x4cc15cffu);
    public ColorRgba MpFill      { get; } = ColorRgba.FromHex(0x4ad9b8ffu);
    public ColorRgba Stamina     { get; } = ColorRgba.FromHex(0xf4a23fffu);
    public ColorRgba TextPrimary { get; } = ColorRgba.FromHex(0xffffffffu);
    public ColorRgba TextMuted   { get; } = ColorRgba.FromHex(0xc4d4ddffu);
    public ColorRgba Warning     { get; } = ColorRgba.FromHex(0xff7b7bffu);

    // --- IThemeHudColors (Phase 9b) ---
    public ColorRgba HudText        { get; } = ColorRgba.FromHex(0xE5E1D6ffu);
    public ColorRgba HudTextShadow  { get; } = new(0f, 0f, 0f, 0.85f);
    public ColorRgba HudAccent      { get; } = ColorRgba.FromHex(0xC9A046ffu);
    public ColorRgba HudBarBg       { get; } = new(80f / 255f, 30f / 255f, 30f / 255f, 0.85f);
    public ColorRgba HudPillBg      { get; } = new(60f / 255f, 60f / 255f, 60f / 255f, 0.50f);

    // --- IThemeMenuColors (Phase 9b) ---
    public ColorRgba MenuBackground { get; } = ColorRgba.FromHex(0x2A2622ffu);
    public ColorRgba MenuText       { get; } = ColorRgba.FromHex(0xE8E2D4ffu);
    public ColorRgba MenuMuted      { get; } = ColorRgba.FromHex(0x8A8070ffu);
    public ColorRgba MenuAccent     { get; } = ColorRgba.FromHex(0xC9A046ffu);
    public ColorRgba MenuBorder     { get; } = new(201f / 255f, 160f / 255f, 70f / 255f, 0.15f);
}
