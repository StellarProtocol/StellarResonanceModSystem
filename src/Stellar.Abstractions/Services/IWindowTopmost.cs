using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Runtime "always on top" for a registered window — the switch behind <see cref="WindowSpec.Topmost"/>. Every
/// <see cref="IWindowControl"/> the framework returns also implements this interface (it is separate only because
/// <see cref="IWindowControl"/> is at the 8-member interface cap). Call it through the
/// <see cref="WindowTopmostExtensions"/> helpers: <c>control.SetTopmost(true)</c> / <c>control.IsTopmost()</c>.
/// <para>A topmost window draws above every non-topmost Stellar window; <see cref="IWindowControl.BringToFront"/> on a
/// normal window never raises it above a topmost one. Meant for small pinned overlays and alerts, not for normal tool
/// windows. Added in 2.30.0 — a plugin that calls it needs framework ≥ 2.30.0.</para>
/// </summary>
public interface IWindowTopmost
{
    /// <summary>True while the window is in the topmost layer (spec default, or the last <see cref="SetTopmost"/>).</summary>
    bool IsTopmost { get; }

    /// <summary>Move the window into (true) or out of (false) the topmost layer. Turning it on also puts it above the
    /// other topmost windows; turning it off drops it back among normal windows by when it was last opened / fronted.
    /// Safe before the window is first shown (applied when it mounts) and kept across hide/show.</summary>
    void SetTopmost(bool topmost);
}

/// <summary>Discoverable access to <see cref="IWindowTopmost"/> from a plain <see cref="IWindowControl"/>.</summary>
public static class WindowTopmostExtensions
{
    /// <summary>Pin (true) or unpin (false) this window always on top. No-op on a handle that doesn't support it
    /// (e.g. the inert handle returned for a duplicate window id).</summary>
    public static void SetTopmost(this IWindowControl control, bool topmost)
        => (control as IWindowTopmost)?.SetTopmost(topmost);

    /// <summary>True while this window is pinned always on top.</summary>
    public static bool IsTopmost(this IWindowControl control)
        => control is IWindowTopmost t && t.IsTopmost;
}
