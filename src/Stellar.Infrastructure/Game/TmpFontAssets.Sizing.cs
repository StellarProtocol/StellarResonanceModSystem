using System;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// Glyph sampling size for the dynamic faces + the per-string face pick shared by styled text and the glyph prewarm.
//
// Why 48/5 and not TMP's default 90/9: every glyph a dynamic face meets for the first time is rasterized into its
// SDF atlas SYNCHRONOUSLY inside layout (GetPreferredWidth/Height, ForceRebuildLayoutImmediate, mesh generation).
// At pt90 that is ~4 ms per glyph, and a CJK/Thai login history meets ~60 new glyphs inside ONE window apply
// (measured 2026-10-23: ap:bubbles=235ms for g:bubbles=#57). The raster cost scales with the glyph cell area
// ((pt + 2·pad)²: 108² → 58², ~3.5× cheaper). Same pad/pt ratio, so the SDF spread (outline/softness headroom)
// is unchanged, and at chat/UI sizes (≤ ~24 px on screen) a 48 pt SDF is visually equivalent to 90 pt — both are
// still DOWN-scaled when drawn (2× vs ~4×), where SDF stays sharp. The 1024² multi-atlas pages are kept (smaller
// cells also fit ~3.5× more glyphs per page).
// The SYSTEM CJK faces (+ Malgun Hangul fallback) go through CreateFontAsset(family, style, pointSize), which
// exposes only the point size — TMP chooses their padding itself.
internal static partial class TmpFontAssets
{
    internal const int SamplingPointSize = 48;
    internal const int AtlasPadding = 5;
    private const int AtlasSize = 1024;
    private const int SdfaaRenderMode = 4165;   // UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA — TMP's default

    private static MethodInfo? _sizedOverload;
    private static bool _sizedResolved;

    /// <summary>The face a string renders from at the given weight — the ONE pick rule styled text and the glyph
    /// prewarm share: Latin + Thai → the shipped merged faces; CJK/kana/Hangul → the system family (when resolved).
    /// Null when no face could be created.</summary>
    internal static TMP_FontAsset? FaceFor(string? s, bool bold)
    {
        var cjk = TextFacePick.For(s) == FaceScript.Cjk;
        return cjk && CjkBold != null
            ? (bold ? CjkBold : CjkRegular ?? CjkBold)
            : bold ? UiBold : UiRegular ?? UiBold;
    }

    // CreateFontAsset(Font, samplingPointSize, atlasPadding, GlyphRenderMode, w, h, AtlasPopulationMode, multiAtlas)
    // takes a GlyphRenderMode, which lives in UnityEngine.TextCoreFontEngineModule — an assembly this project (and
    // the committed CI interop stubs in refs/) does not reference. Resolve that overload by REFLECTION and build the
    // enum argument from its own parameter type, instead of adding a reference + regenerating the stubs. Any failure
    // falls back to the parameterless overload (TMP's pt90/pad9 — the old behaviour), so text never goes missing.
    private static TMP_FontAsset? CreateSized(Font font)
    {
        var m = SizedOverload();
        if (m != null)
        {
            try
            {
                var p = m.GetParameters();
                var args = new object[]
                {
                    font, SamplingPointSize, AtlasPadding, Enum.ToObject(p[3].ParameterType, SdfaaRenderMode),
                    AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, true,
                };
                if (m.Invoke(null, args) is TMP_FontAsset a) return a;
            }
            catch (Exception ex) { StyledTextFactory.Log?.Invoke($"[Window] TMP sized font create failed, using defaults: {ex.GetType().Name}"); }
        }
        return TMP_FontAsset.CreateFontAsset(font);
    }

    private static MethodInfo? SizedOverload()
    {
        if (_sizedResolved) return _sizedOverload;
        _sizedResolved = true;
        try
        {
            foreach (var m in typeof(TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != nameof(TMP_FontAsset.CreateFontAsset)) continue;
                var p = m.GetParameters();
                if (p.Length == 8 && p[0].ParameterType == typeof(Font) && p[1].ParameterType == typeof(int)
                    && p[2].ParameterType == typeof(int) && p[3].ParameterType.IsEnum
                    && p[6].ParameterType == typeof(AtlasPopulationMode) && p[7].ParameterType == typeof(bool))
                { _sizedOverload = m; break; }
            }
        }
        catch { _sizedOverload = null; }
        if (_sizedOverload == null) StyledTextFactory.Log?.Invoke("[Window] TMP sized CreateFontAsset overload not found — shipped faces use TMP defaults (pt90/pad9)");
        return _sizedOverload;
    }
}
