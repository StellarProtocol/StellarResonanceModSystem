using System;
using System.Linq;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Regression pin — framework 2.21.0 (Korean i18n, pre-release) English-UI font change, owner report after
/// relaunching MAIN on 2.21.0: "English UI text changed font". <c>Font.CreateDynamicFontFromOSFont</c> renders
/// each glyph from the FIRST listed family that has it, and the Korean families (Noto Sans CJK KR, Malgun Gothic)
/// had been inserted BEFORE the Latin tail. Malgun Gothic also covers Latin, so on the owner's Proton prefix
/// (no Noto/DejaVu; has Liberation Sans, Arial, malgun.ttf) every Latin glyph moved from Liberation Sans to
/// Malgun Gothic — and on real Windows Malgun would likewise replace Arial. A family that also covers Latin
/// must never precede the Latin families; the Hangul families sit at the END so they only supply glyphs no
/// earlier family has.
/// </summary>
public class FontFamilyOrderTests
{
    private static readonly string[] LatinFamilies = { "Noto Sans", "NotoSans", "DejaVu Sans", "Liberation Sans", "Arial" };
    private static readonly string[] HangulFamilies = { "Malgun Gothic", "Noto Sans CJK KR" };

    // The chain as shipped in framework 2.20.0 (the last release before the Korean catalog).
    private static readonly string[] Pre221Chain =
    {
        "Noto Sans", "NotoSans",
        "Noto Sans CJK JP", "Noto Sans CJK SC",
        "Noto Sans Thai", "Noto Sans Thai UI",
        "DejaVu Sans", "Liberation Sans", "Arial",
    };

    [Fact]
    public void Every_latin_family_precedes_every_hangul_family()
    {
        var chain = OverlayFontFamilies.Chain.ToList();
        foreach (var hangul in HangulFamilies)
        {
            var h = chain.IndexOf(hangul);
            Assert.True(h >= 0, $"'{hangul}' missing from the chain — Korean would tofu");
            foreach (var latin in LatinFamilies)
            {
                var l = chain.IndexOf(latin);
                Assert.True(l >= 0, $"'{latin}' missing from the chain");
                Assert.True(l < h, $"'{latin}' (index {l}) must precede '{hangul}' (index {h}) — else Latin glyphs render in the Korean face");
            }
        }
    }

    [Fact]
    public void Pre_2_21_0_families_keep_their_exact_order_as_the_chain_prefix()
    {
        var chain = OverlayFontFamilies.Chain.ToArray();
        Assert.True(chain.Length >= Pre221Chain.Length);
        Assert.Equal(Pre221Chain, chain.Take(Pre221Chain.Length).ToArray());
    }
}
