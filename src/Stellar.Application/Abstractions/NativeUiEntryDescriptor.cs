namespace Stellar.Application.Abstractions;

/// <summary>
/// Application's view of one allowlist entry — id, display name, and path
/// only. Infrastructure's NativeUiAllowlist projects its richer record onto
/// this so Application doesn't depend on Infrastructure types.
/// </summary>
internal sealed record NativeUiEntryDescriptor(string Id, string DisplayName, string Path)
{
    public bool SafeToHide { get; init; } = true;

    /// <summary>Optional sub-path (relative to <see cref="Path"/>) of the descendant whose own screen-rect is
    /// used as the edit-mode outline / grab-box; null → adapter computes it. See
    /// <c>NativeUiAllowlistEntry.RectChild</c>.</summary>
    public string? RectChild { get; init; }
}
