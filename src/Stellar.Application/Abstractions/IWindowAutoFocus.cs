namespace Stellar.Application.Abstractions;

/// <summary>Optional renderer capability: give keyboard focus to a window's AutoFocus InputElement
/// (<see cref="Stellar.Abstractions.Services.InputElement.AutoFocus"/>) on a user-initiated show. Separate
/// optional interface (mirrors IWindowParking / IWindowPopups / IWindowOrder) so IWindowRenderer stays at the
/// STELLAR0005 member cap; consumed via <c>_renderer as IWindowAutoFocus</c>. A renderer without it (e.g. a test
/// fake) simply never auto-focuses — the caller clears its pending flag and moves on.</summary>
internal interface IWindowAutoFocus
{
    /// <summary>Focus this window's AutoFocus field, if it declared one. Returns true when done — either it was
    /// focused, or the window has no AutoFocus field (nothing to do); returns false when a field exists but its
    /// GameObject isn't active in the hierarchy yet, so the caller should retry on the next tick.</summary>
    bool TryFocusAutoField(object? token);
}
