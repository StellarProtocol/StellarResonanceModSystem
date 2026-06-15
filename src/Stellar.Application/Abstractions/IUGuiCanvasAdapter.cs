using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port implemented in Infrastructure (PandaUGuiAdapter). Builds real
/// uGUI under game canvases and reports anchor/element liveness. The element
/// reference is an opaque token only the adapter interprets.
/// </summary>
internal interface IUGuiCanvasAdapter
{
    /// <summary>True when <paramref name="anchor"/>'s insertion parent currently exists in the scene.</summary>
    bool IsAnchorAvailable(NativeUiAnchor anchor);

    /// <summary>Build + parent the element for <paramref name="spec"/>; null if the anchor is absent.</summary>
    object? Inject(NativeUiElementSpec spec);

    /// <summary>False once the underlying GameObject has been destroyed (e.g. menu closed).</summary>
    bool IsAlive(object? elementRef);

    /// <summary>Re-pull dynamic content (Indicator text, Panel rows/bars) into the live element.</summary>
    void ApplyContent(object? elementRef, NativeUiElementSpec spec);

    /// <summary>Destroy the injected element (no-op if already gone).</summary>
    void Destroy(object? elementRef);
}
