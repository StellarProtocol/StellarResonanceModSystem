using System.Text.RegularExpressions;

namespace Stellar.Application.Services;

/// <summary>
/// Pure text scan deciding whether a ReShade effect (.fx) source is <b>temporal</b>: its output depends on earlier
/// frames, so a fresh effect runtime (the isolated photo capture) must render it for a while before the photo. No I/O.
/// <para>Two signals, either is enough: a uniform fed by a per-frame clock (<c>source = "frametime"</c>, <c>"timer"</c>,
/// <c>"framecount"</c> — every adaptive effect in the measured packs scales its adaptation by one: prod80 Bloom's
/// previous-frame average luma, AcerolaFX AutoExposure, FXShaders NeoBloom / MagicHDR / AdaptiveTonemapper, ArcaneBloom),
/// or a texture named as a history buffer (<c>Prev</c>, <c>Last</c>, <c>History</c>, <c>Accum</c>, <c>Adapt</c> in its
/// name — the same effects' <c>texBPrevAvgLuma</c>, <c>LastAdaptTex</c>, …). A clock uniform also catches effects that
/// merely animate (film grain), which only costs a warm-up. Comments are stripped first.</para>
/// </summary>
internal static class EffectTemporalScanner
{
    private static readonly Regex ClockUniform = new(@"<[^>]*\bsource\s*=\s*""(?:frametime|timer|framecount)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HistoryTexture = new(@"\btexture(?:[123]D)?\s+\w*(?:prev|last|history|accum|adapt)\w*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="fxSource"/> (includes already flattened by the caller) has a clock uniform or a
    /// history texture. False for null — a caller that could not READ the source must decide (it should assume temporal).</summary>
    internal static bool IsTemporal(string? fxSource)
    {
        if (fxSource is null) return false;
        var source = EffectSourceText.WithoutComments(fxSource);
        return ClockUniform.IsMatch(source) || HistoryTexture.IsMatch(source);
    }
}
