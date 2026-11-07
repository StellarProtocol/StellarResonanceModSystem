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
    object? Inject(NativeUiElementSpec spec, UGuiInjectContext context);

    /// <summary>False once the underlying GameObject has been destroyed (e.g. menu closed).</summary>
    bool IsAlive(object? elementRef);

    /// <summary>Re-pull dynamic content (Indicator text, Panel rows/bars) into the live element.</summary>
    void ApplyContent(object? elementRef, NativeUiElementSpec spec);

    /// <summary>Destroy the injected element (no-op if already gone).</summary>
    void Destroy(object? elementRef);
}

/// <summary>
/// Per-injection placement/styling facts the service knows and the spec doesn't (kept off the public
/// <see cref="MenuButtonSpec"/> record — a new ctor param there would break every compiled plugin).
/// </summary>
/// <param name="RailSlot">
/// 0-based position among the live <see cref="NativeUiAnchor.MainMenuRail"/> buttons (branded first, then
/// registration order). Derived from the registration list, NOT from whichever siblings happen to exist at
/// inject time, so re-injects stay stable however a tick orders them. 0 for every other anchor.
/// </param>
/// <param name="Branded">
/// The framework's own Stellar button: accent-tinted icon + pulsing halo. Plugin buttons are never branded —
/// their PNG is drawn as-is so a game-style icon matches the native rail items.
/// </param>
internal readonly record struct UGuiInjectContext(int RailSlot, bool Branded);
