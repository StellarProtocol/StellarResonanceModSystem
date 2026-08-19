using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests;

/// <summary>
/// Pins the per-script face-pick rule for styled overlay text (i18n typography): Thai — even mixed with
/// Latin — and pure Latin render from the shipped merged Latin+Thai face; CJK/kana/Hangul render from the
/// system CJK family. Extracted from TmpStyledText (Stage A inlined it) so the rule is unit-tested.
/// </summary>
public class TextFacePickTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Plugins", false)]                  // Latin
    [InlineData("ภาษา", false)]                     // Thai
    [InlineData("ขนาด UI", false)]                  // Thai + Latin
    [InlineData("言語", true)]                       // kanji
    [InlineData("プリセット", true)]                 // katakana
    [InlineData("UI スケール", true)]                // Latin + kana
    [InlineData("한국어", true)]                     // Hangul
    public void For_picks_the_script_face(string? s, bool expectCjk)
        => Assert.Equal(expectCjk ? FaceScript.Cjk : FaceScript.LatinThai, TextFacePick.For(s));
}
