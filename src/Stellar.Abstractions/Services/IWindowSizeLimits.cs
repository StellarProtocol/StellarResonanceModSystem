using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Runtime resize limits for a <see cref="WindowSpec.Resizable"/> window — the switch behind
/// <see cref="WindowSpec.MinWidth"/>/<see cref="WindowSpec.MinHeight"/>/<see cref="WindowSpec.MaxWidth"/>/<see cref="WindowSpec.MaxHeight"/>,
/// for a window whose allowed size depends on what it currently shows (e.g. a meter whose list view resizes freely but
/// whose party view has a fixed, computed height). Every <see cref="IWindowControl"/> the framework returns also
/// implements this interface (separate because <see cref="IWindowControl"/> is at the 8-member interface cap). Call it
/// through <see cref="WindowSizeLimitsExtensions"/>: <c>control.SetSizeLimits(minW, minH, maxW, maxH)</c>.
/// <para>Lock an axis by passing min == max for it (the resize grip then only changes the other axis). The limits hold
/// for the grip (normal and layout-edit mode), every <see cref="IWindowControl.SetRect"/> and every saved-layout
/// restore, and survive hide/show and remounts. A live size outside the new limits is clamped AT ONCE, size only:
/// the window's top-left never moves. A content minimum (<c>RowElement.ConstrainsWindowMinWidth</c>) still raises
/// the minimum width. No-op on a non-resizable window. Cheap; call it only when the limits change, not per tick.
/// Added in 2.33.0 — a plugin that calls it needs framework ≥ 2.33.0.</para>
/// </summary>
public interface IWindowSizeLimits
{
    /// <summary>Replace the window's resize limits (canvas units, like <see cref="WindowSpec.MinWidth"/>). A max below
    /// its min is raised to the min. Safe before the window is first shown (applied when it mounts).</summary>
    void SetSizeLimits(float minWidth, float minHeight, float maxWidth, float maxHeight);
}

/// <summary>Discoverable access to <see cref="IWindowSizeLimits"/> from a plain <see cref="IWindowControl"/>.</summary>
public static class WindowSizeLimitsExtensions
{
    /// <summary>Change this window's resize limits at runtime (see <see cref="IWindowSizeLimits"/>). No-op on a handle
    /// that doesn't support it (an older framework, or the inert handle returned for a duplicate window id).</summary>
    public static void SetSizeLimits(this IWindowControl control, float minWidth, float minHeight, float maxWidth, float maxHeight)
        => (control as IWindowSizeLimits)?.SetSizeLimits(minWidth, minHeight, maxWidth, maxHeight);
}
