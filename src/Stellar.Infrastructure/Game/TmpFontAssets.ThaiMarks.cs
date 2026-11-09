using System;
using TMPro;
using FeatureTag = UnityEngine.TextCore.OTL_FeatureTag;   // TMP_Text.fontFeatures element (NOT TMPro.OTL_FeatureTag)

namespace Stellar.Infrastructure.Game;

// Thai stacked marks on the shipped faces ("ปลั๊กอิน": the tone mark ๊ must sit ON TOP of ั, not beside it).
// The shipped faces carry no presentation-form glyphs; Noto Sans Thai stacks its marks only through GPOS
// mark-to-base / mark-to-mark anchors. This game's TMP (3.2-pre, Unity 2022.3) does apply those anchors, and its
// IsBaseGlyph excludes the Thai marks, but three things had to line up, and none did (root-caused 2026-11-09):
//   1. The TEXT must have the 'mark' + 'mkmk' features active. A new TextMeshProUGUI copies them from the game's
//      TMP_Settings asset in LoadDefaultSettings (class default { 0 } = none), so we set them explicitly.
//   2. The FONT ASSET must hold the records. On-demand glyph adds only fetch them when getFontFeatures AND the game's
//      TMP_Settings.getFontFeaturesAtRuntime are set, and only AMONG the glyphs of one batch (see ThaiMarkSeed). So
//      the first Thai string seeds every Thai base + mark in ONE TryAddCharacters(includeFontFeatures: true) call,
//      which fetches records ungated.
//   3. The FONT FILE must expose them in a form the native FontEngine reads: the faces' Thai mark/mkmk lookups used
//      to be GPOS Extension lookups (type 9). The faces were rewritten with tools/fonts/thai_gpos_unwrap.py so those
//      lookups are plain type 4 / 6 (pinned by ShippedThaiFontGposTests).
internal static partial class TmpFontAssets
{
    private static bool _thaiSeeded;

    /// <summary>Before a string renders through a shipped face: if it contains Thai and the faces have not been
    /// seeded yet, import every Thai glyph + its mark records into BOTH shipped faces in one batch each. One-time
    /// cost (about 73 glyphs per face, roughly tens of ms each face at pt48), paid only by a process that shows Thai.
    /// Must run BEFORE any Thai glyph reaches a face: a glyph already in the atlas is skipped by TryAddCharacters and
    /// would keep no records.</summary>
    internal static void PrepareThai(string? s)
    {
        if (_thaiSeeded || !GlyphScript.IsThai(s)) return;
        _thaiSeeded = true;
        Seed(UiRegular, "regular");
        Seed(UiBold, "bold");
    }

    private static void Seed(TMP_FontAsset? face, string label)
    {
        if (face == null) return;
        try
        {
            face.getFontFeatures = true;
            face.TryAddCharacters(ThaiMarkSeed.Characters, out string missing, true);
            UploadAllPages(face);
            var ft = face.fontFeatureTable;
            var m2b = ft?.MarkToBaseAdjustmentRecords?.Count ?? -1;
            var m2m = ft?.MarkToMarkAdjustmentRecords?.Count ?? -1;
            // In-game evidence for the fix: both counts must be > 0 (≈ hundreds) for the Thai faces.
            StyledTextFactory.Log?.Invoke($"[Window] Thai marks seeded ({label}): markToBase={m2b} markToMark={m2m}"
                + (string.IsNullOrEmpty(missing) ? "" : $" missing={missing.Length}"));
        }
        catch (Exception ex) { StyledTextFactory.Log?.Invoke($"[Window] Thai mark seed failed ({label}): {ex.GetType().Name}"); }
    }

    // Same reason as SeedDecorationGlyph / TmpGlyphPrewarm.UploadTouched: glyphs added via TryAddCharacters can sit in
    // the lookup table with their texels not yet uploaded, so flush every page the batch may have touched.
    private static void UploadAllPages(TMP_FontAsset face)
    {
        try
        {
            var pages = face.atlasTextures;
            if (pages == null) return;
            for (var p = 0; p < pages.Length && p < face.atlasTextureCount; p++)
                if (pages[p] != null) pages[p].Apply(false, false);
        }
        catch { /* non-readable atlas: best effort */ }
    }

    /// <summary>Turn on GPOS mark-to-base + mark-to-mark positioning for a TMP text drawn with our faces, keeping
    /// whatever features the game's TMP_Settings already gave it (e.g. kern). A string-free fix, so input-field caret
    /// and selection indices, measuring and ellipsis are unaffected.</summary>
    internal static void EnableMarkPositioning(TMP_Text t)
    {
        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<FeatureTag>();
            var cur = t.fontFeatures;
            if (cur != null)
                for (var i = 0; i < cur.Count; i++)
                    if ((uint)cur[i] != 0 && !list.Contains(cur[i])) list.Add(cur[i]);   // 0 = the "none" placeholder
            if (!list.Contains(FeatureTag.mark)) list.Add(FeatureTag.mark);
            if (!list.Contains(FeatureTag.mkmk)) list.Add(FeatureTag.mkmk);
            t.fontFeatures = list;
        }
        catch { /* marks stay unpositioned (the old look); text itself is unaffected */ }
    }
}
