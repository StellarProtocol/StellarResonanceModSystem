using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Provides the embedded theme assets (font bytes + bar textures) to ThemeRenderer.
/// Implementation in Infrastructure reads from assembly resources and generates
/// Texture2D instances. Application doesn't see Texture2D; it just asks for the
/// font byte stream and bar-pixel arrays, which ThemeRenderer turns into Unity
/// objects.
/// </summary>
internal interface IThemeAssetProvider
{
    /// <summary>Returns the embedded Noto Sans TTF byte stream.</summary>
    byte[] LoadFontBytes();

    /// <summary>
    /// Returns a 1×1 pixel buffer (RGBA32) for the given colour. ThemeRenderer
    /// turns this into a Unity Texture2D for use as GUIStyle background.
    /// </summary>
    byte[] MakeSolidTexturePixels(ColorRgba colour);
}
