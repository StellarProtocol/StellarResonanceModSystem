using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Descriptor for a bindable keyboard action declared via <see cref="Services.IHotkeys.DeclareAction"/>.</summary>
/// <param name="Id">Stable string id unique within the declaring plugin (e.g. "ToggleWindow").</param>
/// <param name="Description">Human-readable label shown in the Settings → Hotkeys panel.</param>
/// <param name="SuggestedDefault">Optional default binding suggested to the user; may be null (no default).</param>
public sealed record HotkeyAction(string Id, string Description, KeyBinding? SuggestedDefault)
{
    /// <summary>
    /// Optional "only while" gate, polled on the main thread every framework tick. While it returns false the action
    /// is dormant: a press does NOT invoke the callback, and the key is NOT held back from the game even when Settings →
    /// Hotkeys "Don't pass hotkeys through to the game" is on — the press reaches the game as if nothing were bound.
    /// While it returns true (or when null, the default) the action behaves exactly as before. Use it for keys that only
    /// mean something while your window is open (e.g. <c>() =&gt; window.IsShown</c>). Keep it cheap and allocation-free.
    /// A throw counts as false. The binding itself is unaffected (still listed, still claims its chord).
    /// Since 2.26.0 (an <c>init</c> property, not a constructor parameter, so existing callers keep binding).
    /// </summary>
    public Func<bool>? IsActive { get; init; }
}
