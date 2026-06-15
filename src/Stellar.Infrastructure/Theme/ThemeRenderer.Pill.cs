using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Gold "Lv 78"-style pill drawing. Lives in its own partial so the main
/// <see cref="ThemeRenderer"/> file stays under STELLAR0001's 500 LoC cap.
/// FontScale-aware: pill height tracks LineBody (which scales with the
/// user's FontScale) and the font size scales proportionally so the pill
/// stays readable + aligned to body text at any slider position.
/// </summary>
internal sealed partial class ThemeRenderer
{
    // The rounded-rect bakers now live in the shared, side-effect-free
    // RoundedTextureBaker so the uGUI HUD sprite provider rounds corners with
    // the identical AA formula. These thin wrappers keep every existing IMGUI
    // call site (Pill/HudOverlay/ChromeStyles bakes) unchanged.
    private static Texture2D MakeRoundedTexture(int size, int radius, ColorRgba colour)
        => RoundedTextureBaker.Rounded(size, radius, colour);

    private static Texture2D MakeRoundedBorderedTexture(int size, int radius, int borderPx, ColorRgba fill, ColorRgba border)
        => RoundedTextureBaker.RoundedBordered(size, radius, borderPx, fill, border);
}
