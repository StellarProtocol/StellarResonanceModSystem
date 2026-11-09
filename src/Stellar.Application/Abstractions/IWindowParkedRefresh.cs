namespace Stellar.Application.Abstractions;

/// <summary>Optional renderer capability: a window the renderer parks WITHOUT deactivating its root (a "soft park" —
/// own canvas off, moved off-screen, see WindowRenderer.SoftPark.cs) can keep its bindings current while hidden, so
/// its next open re-pulls nothing that changed meanwhile (world entry, a language switch). Separate optional interface
/// (mirrors IWindowParking / IWindowAutoFocus) so IWindowRenderer stays at the STELLAR0005 member cap. A renderer
/// without it (a test fake) never refreshes a hidden window — the old "hidden runs zero value funcs" behaviour.</summary>
internal interface IWindowParkedRefresh
{
    /// <summary>Per capped (~10 Hz) apply tick of a HIDDEN parked window: true when the renderer wants a background
    /// apply of it now (soft-parked, and due or with background work pending). Cheap; no plugin code runs.</summary>
    bool WantsParkedRefresh(object? token);

    /// <summary>One background apply of a soft-parked window: bindings run, bubble measures are budgeted, nothing is
    /// shown or moved. The caller has already checked the window's ShouldRender gate.</summary>
    void RefreshParked(object? token);
}
