using System.Collections.Generic;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Picks which pre-drawn bitmap of a framework glyph to draw (icon art rule, 2026-11-09: every icon is pre-drawn and
/// pixel-hinted per size — 12/16/24/32/64/128 — and never one master scaled down). Pure + Unity-free so it is unit
/// tested; <see cref="LauncherIcons"/> feeds it the on-screen size = glyph canvas units × the window canvas
/// scaleFactor (resolution × UI scale).
/// </summary>
internal static class IconSizePicker
{
    // A float target counts as "the same size" within this much of a whole size: 16 units × 0.75 = 12.000001 must still
    // be the 12 px art. Wider would draw a bitmap 1:1-flagged (no mips) at a visibly different size.
    internal const float ExactTolerance = 0.05f;

    /// <summary>The on-screen pixel size of a glyph <paramref name="canvasUnits"/> wide on a canvas scaled by
    /// <paramref name="scale"/> (screen px per canvas unit; a non-positive scale means unscaled).</summary>
    public static float DisplayPx(float canvasUnits, float scale) => canvasUnits * (scale > 0f ? scale : 1f);

    /// <summary>
    /// The pre-drawn size to draw at <paramref name="targetPx"/>, from <paramref name="available"/> (ascending).
    /// <list type="number">
    /// <item>A size equal to the target (within <see cref="ExactTolerance"/>) → it, <c>Exact = true</c>: drawn 1:1.</item>
    /// <item>Otherwise the SMALLEST size larger than the target → <c>Exact = false</c>: the caller samples it with
    /// mipmaps. Minifying, never magnifying, keeps strokes solid; the worst ratio in the 12…128 set is ~0.5.</item>
    /// <item>A target above every size → the largest (magnified; nothing better exists).</item>
    /// </list>
    /// Size -1 when <paramref name="available"/> is empty (the caller falls back to the un-sized bitmap).
    /// </summary>
    public static (int Size, bool Exact) Pick(float targetPx, IReadOnlyList<int> available)
    {
        if (available.Count == 0) return (-1, false);
        for (var i = 0; i < available.Count; i++)
        {
            var size = available[i];
            if (System.Math.Abs(size - targetPx) <= ExactTolerance) return (size, true);
            if (size > targetPx) return (size, false);
        }
        return (available[available.Count - 1], false);
    }
}
