namespace Stellar.Infrastructure.Game;

/// <summary>
/// Script classification for overlay text, used to gate Unity's synthetic (faux) bold. The dynamic OS
/// overlay font (<see cref="WindowThemeAssets.MenuFont"/>) has no real bold face under Proton/IL2CPP, so
/// <c>FontStyle.Bold</c> is emboldened algorithmically. That faux-bold thickening mangles complex-script
/// glyphs — CJK ideographs, Japanese kana, Hangul, and Thai stacked vowels/tone marks — into an
/// unreadable blur, while Latin faux-bold renders fine (this was the i18n P0 JA/TH bold-header bug:
/// 言語 / プリセット, ภาษา / พรีเซ็ต rendered broken while regular-weight text was fine). Emphasis on a string
/// that contains any such glyph must therefore drop to regular weight — the regular glyphs render
/// correctly (proven in-game by the P0 font gate). Pure BCL (no Unity), so it is unit-tested in CI.
/// </summary>
internal static class GlyphScript
{
    /// <summary>
    /// True when <paramref name="s"/> contains at least one code point from a script whose glyphs are
    /// distorted by Unity's synthetic bold (CJK / Japanese kana / Hangul / Thai). Null, empty, and
    /// pure-Latin (incl. Bahasa Indonesia) strings return false. Surrogate pairs are decoded so
    /// supplementary-plane CJK ideographs are recognised.
    /// </summary>
    public static bool HasSyntheticBoldRisk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(c, s[i + 1]);
                i++;   // consumed the low surrogate
            }
            else
            {
                cp = c;
            }
            if (IsRiskCodePoint(cp)) return true;
        }
        return false;
    }

    // Script blocks whose glyphs synthetic bold ruins. Kept as explicit ranges (not char.GetUnicodeCategory)
    // so the gate is exact and cheap on the per-text-change hot path.
    private static bool IsRiskCodePoint(int cp) =>
        (cp >= 0x0E00 && cp <= 0x0E7F) ||   // Thai
        (cp >= 0x1100 && cp <= 0x11FF) ||   // Hangul Jamo
        (cp >= 0x3000 && cp <= 0x303F) ||   // CJK Symbols & Punctuation (、。「」〜 …)
        (cp >= 0x3040 && cp <= 0x30FF) ||   // Hiragana + Katakana
        (cp >= 0x3130 && cp <= 0x318F) ||   // Hangul Compatibility Jamo
        (cp >= 0x31F0 && cp <= 0x31FF) ||   // Katakana Phonetic Extensions
        (cp >= 0x3400 && cp <= 0x4DBF) ||   // CJK Unified Ideographs Extension A
        (cp >= 0x4E00 && cp <= 0x9FFF) ||   // CJK Unified Ideographs
        (cp >= 0xAC00 && cp <= 0xD7AF) ||   // Hangul Syllables
        (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK Compatibility Ideographs
        (cp >= 0xFF00 && cp <= 0xFFEF) ||   // Halfwidth & Fullwidth Forms
        (cp >= 0x20000 && cp <= 0x2FA1F);   // CJK Unified Ideographs Extension B..F + Compatibility Supplement
}
