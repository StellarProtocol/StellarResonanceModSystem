using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Rounded meter-row top line overflow rule (pure, unit-tested without Unity): given the width left for the
/// name + spec after everything else on the line (rank, crest, flag, spacer, voice, share %, imagines, gaps), decide
/// whether the spec stays and how wide the name may be. The spec drops FIRST; only then is the name cut (the caller
/// cuts it to the returned width with the framework's prefix + "..." — never TMP Ellipsis).
/// </summary>
internal static class MeterTopLineFit
{
    /// <param name="avail">Line width minus every other child's preferred width and gap, INCLUDING the name's own
    /// gap; the spec's gap is not included (it only exists while the spec is shown).</param>
    /// <param name="nameFull">The full (uncut) name's preferred width.</param>
    /// <param name="specWanted">The row wants a spec (ShowSpec and a non-empty spec).</param>
    /// <param name="specFull">The spec label's preferred width ("· Spec").</param>
    /// <param name="spacing">The line's HorizontalLayoutGroup spacing (the spec's gap).</param>
    /// <returns>ShowSpec, and NameW = the width the name gets (== nameFull → no cut; less → cut to it).</returns>
    public static (bool ShowSpec, float NameW) Decide(float avail, float nameFull, bool specWanted, float specFull, float spacing)
    {
        if (specWanted && nameFull + spacing + specFull <= avail) return (true, nameFull);
        if (nameFull <= avail) return (false, nameFull);
        return (false, MathF.Max(0f, MathF.Floor(avail)));   // whole px: the cut is measured against it
    }
}
