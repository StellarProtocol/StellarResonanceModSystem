namespace Stellar.Application.Abstractions;

/// <summary>
/// Application's view of one allowlist entry — id, display-name KEY, and path
/// only. Infrastructure's NativeUiAllowlist projects its richer record onto
/// this so Application doesn't depend on Infrastructure types. DisplayNameKey
/// is a framework localization key (<c>gameui.entry.*</c>), resolved at render
/// time by the consumers below — not display text itself.
/// </summary>
internal sealed record NativeUiEntryDescriptor(string Id, string DisplayNameKey, string Path)
{
    public bool SafeToHide { get; init; } = true;

    /// <summary>Optional sub-path (relative to <see cref="Path"/>) of the descendant whose own screen-rect is
    /// used as the edit-mode outline / grab-box; null → adapter computes it. See
    /// <c>NativeUiAllowlistEntry.RectChild</c>.</summary>
    public string? RectChild { get; init; }
}
