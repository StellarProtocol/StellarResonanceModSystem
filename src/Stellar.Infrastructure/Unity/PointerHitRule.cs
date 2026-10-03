using System;

namespace Stellar.Infrastructure.Unity;

/// <summary>Whether a registered pointer area takes a mouse press. Windows close by <c>SetActive(false)</c> and keep
/// their registered areas, so an area must be on screen (<c>activeInHierarchy</c>) as well as under the pointer and not
/// covered by a window in front — otherwise a closed window's control fires under whatever the player clicks there.</summary>
internal static class PointerHitRule
{
    public static bool AcceptsPress(bool alive, bool activeInHierarchy, bool containsPointer, Func<bool> frontWindowBlocks)
        => alive && activeInHierarchy && containsPointer && !frontWindowBlocks();
}
