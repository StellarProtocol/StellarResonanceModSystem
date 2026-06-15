namespace Stellar.Abstractions.Domain;

/// <summary>Descriptor for a bindable keyboard action declared via <see cref="Services.IHotkeys.DeclareAction"/>.</summary>
/// <param name="Id">Stable string id unique within the declaring plugin (e.g. "ToggleWindow").</param>
/// <param name="Description">Human-readable label shown in the Settings → Hotkeys panel.</param>
/// <param name="SuggestedDefault">Optional default binding suggested to the user; may be null (no default).</param>
public sealed record HotkeyAction(string Id, string Description, KeyBinding? SuggestedDefault);
