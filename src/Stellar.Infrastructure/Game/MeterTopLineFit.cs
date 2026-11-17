using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Rounded meter-row top line overflow rule (pure, unit-tested without Unity): given the width left for the
/// name + class + spec after everything else on the line (rank, crest, flag, spacer, voice, share %, imagines, gaps),
/// decide which optional labels stay and how wide the name may be. Drop order: spec FIRST, then class, and only then
/// is the name cut (the caller cuts it to the returned width with the framework's prefix + "..." — never TMP Ellipsis).
/// Spec goes before class because CombatMeter's "Both" shows "Class · Spec" and the class is the coarser, shorter fact.
/// </summary>
internal static class MeterTopLineFit
{
    /// <param name="avail">Line width minus every other child's preferred width and gap, INCLUDING the name's own
    /// gap; the class's and spec's gaps are not included (each only exists while its label is shown).</param>
    /// <param name="nameFull">The full (uncut) name's preferred width.</param>
    /// <param name="cls">The class label: wanted (ShowClassName and a non-empty class name) + its preferred width.</param>
    /// <param name="spec">The spec label: wanted (ShowSpec and a non-empty spec) + its preferred width ("· Spec").</param>
    /// <param name="spacing">The line's HorizontalLayoutGroup spacing (one gap per shown label).</param>
    /// <returns>ShowClass, ShowSpec, and NameW = the width the name gets (== nameFull → no cut; less → cut to it).
    /// An unwanted label is never reported as shown.</returns>
    public static (bool ShowClass, bool ShowSpec, float NameW) Decide(
        float avail, float nameFull, Label cls, Label spec, float spacing)
    {
        float clsW = cls.Wanted ? spacing + cls.Width : 0f;
        if (spec.Wanted && nameFull + clsW + spacing + spec.Width <= avail) return (cls.Wanted, true, nameFull);
        if (cls.Wanted && nameFull + clsW <= avail) return (true, false, nameFull);
        if (nameFull <= avail) return (false, false, nameFull);
        return (false, false, MathF.Max(0f, MathF.Floor(avail)));   // whole px: the cut is measured against it
    }

    /// <summary>One optional top-line label (value type: passing it allocates nothing on the poll path).</summary>
    public readonly record struct Label(bool Wanted, float Width);
}
