using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Effect PLACEHOLDER concern of <see cref="IGameAssets"/> (which inherits this; reach it as <c>_services.GameAssets</c>).
/// Split out so <see cref="IGameAssets"/> stays within the framework's interface-width budget. Added in 2.34.0.
/// </summary>
public interface IEffectPlaceholders
{
    /// <summary>
    /// A placeholder tile for an effect that has no icon of its own: a white glyph for <paramref name="category"/>
    /// (<see cref="IGameText.GetEffectCategory"/>) on a green (buff) or red (<paramref name="debuff"/>) tile, the
    /// same art CombatMeter 3.0 draws. Feed it to the same elements as <see cref="IGameAssets.LoadBuffIcon"/> results
    /// (<c>GameTextureElement</c>, <c>CooldownTileElement</c>, ...). The art is pre-drawn and pixel-hinted per size
    /// (14-64 px, never one master scaled): this returns the smallest pre-drawn size at or above
    /// <paramref name="sizePx"/> (the 64 px tile above that), so pass the icon's ON-SCREEN pixel size. Synchronous:
    /// decoded from the framework's embedded PNGs on first use per (category, kind, size), then cached; null only
    /// when the resource is missing. <paramref name="uv"/> is always <c>(0,0,1,1)</c>. Main thread only. Added in 2.34.0.
    /// </summary>
    /// <param name="category">The glyph; an unknown value draws <see cref="EffectCategory.Default"/>.</param>
    /// <param name="debuff">True = red debuff tile, false = green buff tile.</param>
    /// <param name="sizePx">The icon's on-screen size in pixels.</param>
    /// <param name="uv">Always the full rect <c>(0,0,1,1)</c>.</param>
    object? LoadEffectPlaceholder(EffectCategory category, bool debuff, int sizePx, out UvRect uv);

    /// <summary>The PNG bytes of the <see cref="LoadEffectPlaceholder"/> tile, for elements that take
    /// <c>Func&lt;byte[]?&gt;</c> (e.g. <c>ImageElement</c>). Same size pick; the array is cached and shared, do not
    /// modify it. Null only when the resource is missing. Any thread. Added in 2.34.0.</summary>
    /// <param name="category">The glyph; an unknown value draws <see cref="EffectCategory.Default"/>.</param>
    /// <param name="debuff">True = red debuff tile, false = green buff tile.</param>
    /// <param name="sizePx">The image's on-screen size in pixels.</param>
    byte[]? GetEffectPlaceholderPng(EffectCategory category, bool debuff, int sizePx);
}
