namespace Stellar.Application.Abstractions;

/// <summary>Optional renderer capability behind <see cref="Stellar.Abstractions.Services.IWindowHost.PrewarmText"/>:
/// queue text whose glyphs the renderer should rasterize ahead of layout, a few per frame. Separate optional
/// interface (mirrors IWindowAutoFocus / IWindowParking) so IWindowRenderer stays at the STELLAR0005 member cap;
/// consumed via <c>_renderer as IWindowTextPrewarm</c>. A renderer without it (a test fake, the sandbox) makes the
/// hint a no-op.</summary>
internal interface IWindowTextPrewarm
{
    /// <summary>Queue <paramref name="text"/> for glyph prewarm. Thread-safe; null/empty ignored.</summary>
    void PrewarmText(string text);
}
