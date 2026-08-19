using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins <see cref="GlyphScript.HasSyntheticBoldRisk"/> — the gate that drops Unity's synthetic (faux)
/// bold for complex-script strings. Origin: i18n P0 bold-header bug (framework 2.1.0) — JA/TH bold
/// section headers rendered unreadable because faux-bold distorts CJK/kana/Thai glyphs; Latin faux-bold
/// is fine. A false negative here re-introduces the unreadable-headers regression, a false positive
/// needlessly drops bold from Latin headers. Static/pure — no IL2CPP / game process needed.
/// </summary>
public sealed class GlyphScriptTests
{
    [Theory]
    // Latin (English + Bahasa Indonesia) → keep bold (no risk).
    [InlineData("Language")]
    [InlineData("Preset")]
    [InlineData("Font Scale")]
    [InlineData("Bahasa")]
    [InlineData("PLUGINS")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("123 4.5s")]
    [InlineData("✕")]                 // close glyph — not a complex script
    public void Latin_and_symbols_are_not_at_risk(string? s)
        => Assert.False(GlyphScript.HasSyntheticBoldRisk(s));

    [Theory]
    // Japanese — the exact headers from the owner's bug report.
    [InlineData("言語")]               // Language
    [InlineData("プリセット")]           // Preset (katakana)
    [InlineData("フォントスケール")]       // Font Scale
    [InlineData("設定")]               // window title
    [InlineData("、。「」")]              // CJK punctuation used in JA
    // Thai — stacked vowels/tone marks are what faux-bold ruins worst.
    [InlineData("ภาษา")]              // Language
    [InlineData("พรีเซ็ต")]             // Preset (has a tone mark)
    [InlineData("ขนาดฟอนต์")]          // Font Scale
    // Korean (Hangul) — same faux-bold distortion class, covered for future locales.
    [InlineData("언어")]
    public void Cjk_kana_thai_hangul_are_at_risk(string s)
        => Assert.True(GlyphScript.HasSyntheticBoldRisk(s));

    [Fact]
    public void Mixed_latin_and_thai_is_at_risk()
        // A header like "UI เกม" (Latin "UI" + Thai) must drop bold — the Thai run would faux-bold.
        => Assert.True(GlyphScript.HasSyntheticBoldRisk("UI เกม"));

    [Fact]
    public void Supplementary_plane_cjk_ideograph_is_at_risk()
        // U+20000 (𠀀), a CJK Extension B ideograph encoded as a surrogate pair, must be decoded + flagged.
        => Assert.True(GlyphScript.HasSyntheticBoldRisk("\U00020000"));

    [Fact]
    public void Lone_high_surrogate_does_not_throw_and_is_not_risk()
        // Defensive: a dangling high surrogate (malformed string) must be handled as a BMP char, not crash.
        => Assert.False(GlyphScript.HasSyntheticBoldRisk("A\uD840"));

    [Theory]
    [InlineData("ภาษา")]
    [InlineData("พรีเซ็ต")]
    [InlineData("UI เกม")]   // mixed Latin + Thai → still Thai (gets the real bold Thai face)
    public void Thai_strings_are_thai(string s) => Assert.True(GlyphScript.IsThai(s));

    [Theory]
    [InlineData("言語")]      // Japanese — NOT Thai (routes to larger-regular, not the Thai bold font)
    [InlineData("Language")] // Latin
    [InlineData("언어")]      // Korean
    [InlineData("")]
    [InlineData(null)]
    public void Non_thai_strings_are_not_thai(string? s) => Assert.False(GlyphScript.IsThai(s));
}
