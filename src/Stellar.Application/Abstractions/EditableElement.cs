using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>One element the layout editor can outline + toggle: its id, current screen rect, whether it is
/// shown, and whether it may be hidden (false = unsafe-to-hide game HUD). Emitted by NativeUiService /
/// HudService / WindowService for the edit-mode chrome.</summary>
internal readonly record struct EditableElement(string Id, WindowRect Rect, bool Visible, bool CanHide);
