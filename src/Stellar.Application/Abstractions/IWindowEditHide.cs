namespace Stellar.Application.Abstractions;

/// <summary>Optional renderer capability (mirrors IWindowParking / IWindowParkedRefresh, keeping IWindowRenderer under
/// the STELLAR0005 member cap): the layout editor's TRANSIENT "Hide others" for one window. It must beat the renderer's
/// edit-mode force-show of EditModeDragOnly overlays, which otherwise overrides every <c>hide</c> passed to ApplyValues
/// while editing. Never persisted. A renderer without it (a test fake) only sees the OR-ed <c>hide</c> flag.</summary>
internal interface IWindowEditHide
{
    /// <summary>Set the window's transient edit-mode hide; takes effect on the next ApplyValues.</summary>
    void SetEditHidden(object? token, bool hidden);
}
