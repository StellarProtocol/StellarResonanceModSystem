namespace Stellar.Application.Abstractions;

/// <summary>Lifecycle control for transient popups the renderer owns outside the normal window tree. The
/// generic <c>DropdownElement</c> popup lives on its OWN scene-root overlay canvas (not a child of any window),
/// so hiding the owning window does NOT tear it down — a lifecycle owner (e.g. the layout editor on edit-exit)
/// must explicitly close it. Separate optional interface (mirrors IWindowCanvasMetrics / IWindowOrder) so
/// IWindowRenderer stays at the STELLAR0005 member cap; consumed via <c>_renderer as IWindowPopups</c>.</summary>
internal interface IWindowPopups
{
    /// <summary>Close any open dropdown popup (destroys its scene-root layer). Idempotent — a no-op when none
    /// is open or the builder/canvas has been torn down.</summary>
    void CloseOpenPopups();
}
