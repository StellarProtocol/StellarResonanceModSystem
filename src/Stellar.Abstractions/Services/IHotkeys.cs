using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Plugin-facing hotkey service. Declare bindable keyboard actions and receive callbacks when pressed.</summary>
public interface IHotkeys
{
    /// <summary>
    /// Declare a bindable action. The framework resolves the binding from user config
    /// (Phase 9) or falls back to <see cref="HotkeyAction.SuggestedDefault"/>.
    /// Dispose the returned handle to unregister the action.
    /// </summary>
    IHotkeyAction DeclareAction(HotkeyAction action, System.Action callback);
}
