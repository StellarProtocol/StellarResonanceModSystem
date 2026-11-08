using System;

namespace Stellar.Infrastructure.Unity;

/// <summary>Whether a registered pointer area takes a mouse press. Windows close by <c>SetActive(false)</c> and keep
/// their registered areas, so an area must be on screen (<c>activeInHierarchy</c>) as well as under the pointer and not
/// covered by a window in front — otherwise a closed window's control fires under whatever the player clicks there.</summary>
internal static class PointerHitRule
{
    public static bool AcceptsPress(bool alive, bool activeInHierarchy, bool containsPointer, Func<bool> frontWindowBlocks)
        => alive && activeInHierarchy && containsPointer && !frontWindowBlocks();

    /// <summary>Whether an active window in FRONT of a hit-test's own window counts as covering the point. A
    /// <c>Passive</c> window (<c>WindowSpec.Passive</c>, click-through: no raycast blocker) is see-through to input in
    /// play, so it must not shadow what's behind it — a wide passive banner (RaidManager's mechanic alert) otherwise
    /// blocked drag + hover on every window under it. In layout edit mode a passive window is a normal editable window
    /// (selected + dragged like any other), so there it still covers.</summary>
    public static bool FrontWindowCovers(bool passive, bool layoutEditing) => !passive || layoutEditing;
}
