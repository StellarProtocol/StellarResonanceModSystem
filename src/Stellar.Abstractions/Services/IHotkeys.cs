using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Plugin-facing hotkey service. Declare bindable keyboard actions and receive callbacks when pressed.</summary>
public interface IHotkeys
{
    /// <summary>
    /// Declare a bindable action. The framework resolves the binding from user config
    /// (Phase 9) or falls back to <see cref="HotkeyAction.SuggestedDefault"/>. A binding the player saved always beats
    /// another action's suggested default, so a chord is never held by two actions.
    /// Dispose the returned handle to unregister the action.
    /// </summary>
    IHotkeyAction DeclareAction(HotkeyAction action, System.Action callback);

    /// <summary>
    /// One-time move of a binding the player SAVED for <paramref name="actionId"/>: if it equals
    /// <paramref name="from"/>, it becomes <paramref name="to"/> — or "unbound" when another declared action already
    /// holds <paramref name="to"/>. Suggested defaults are never saved, so changing a default needs no migration. Call it
    /// before declaring the action, and remember (in your own config) that you ran it.
    /// </summary>
    /// <param name="actionId">The action id, as passed to <see cref="DeclareAction"/>.</param>
    /// <param name="from">The old chord.</param>
    /// <param name="to">The new chord.</param>
    SavedBindingMigration MigrateSavedBinding(string actionId, KeyBinding from, KeyBinding to);

    /// <summary>
    /// True while the key bound to <paramref name="actionId"/> is held down with exactly its modifiers — a level
    /// query for actions that repeat while held (poll it from your <c>Update</c>; the callback still fires once per
    /// press). False for an unbound or undeclared action. Since 2.20.0 (the default implementation, for test doubles, always returns false).
    /// </summary>
    /// <param name="actionId">The action id, as passed to <see cref="DeclareAction"/>.</param>
    bool IsActionHeld(string actionId) => false;
}
