using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Thai stacked marks on the shipped TMP faces ("ปลั๊กอิน": ๊ must stack ON ั). TMP positions the marks from GPOS
/// records it imports per glyph batch, so the one-batch seed must hold every base + mark of real UI words, and the
/// shipped faces must expose their Thai mark/mkmk lookups as PLAIN lookups (not Extension, type 9) — the form the
/// native FontEngine is known to read. See TmpFontAssets.ThaiMarks.cs / tools/fonts/thai_gpos_unwrap.py.
/// </summary>
public class ThaiMarksTests
{
    [Theory]
    [InlineData("ปลั๊กอิน")]          // ั + ๊ stacked on ล (the reported bug)
    [InlineData("ที่ติดตั้ง")]         // ี + ่, ิ, ั + ้
    [InlineData("ป้")]                // tone on a tall consonant
    [InlineData("ปิ่น")]              // upper vowel + tone on a tall consonant
    [InlineData("ฏุ")]                // lower vowel under a descender consonant
    [InlineData("ทั่วไป")]
    [InlineData("ฤๅษี")]
    public void Seed_covers_every_base_and_mark_of_the_word(string word)
    {
        foreach (var c in word)
            Assert.Contains(c, ThaiMarkSeed.Characters);
    }

    [Fact]
    public void Seed_holds_all_consonants_and_marks_once_and_no_digits()
    {
        var s = ThaiMarkSeed.Characters;
        Assert.Equal(s.Length, s.Distinct().Count());
        for (var c = 'ก'; c <= 'ฮ'; c++) Assert.Contains(c, s);      // ก..ฮ
        foreach (var c in "ัิีึืฺุู็่้๊๋์ํ๎")
        {
            Assert.True(ThaiMarkSeed.IsMark(c));
            Assert.Contains(c, s);
        }
        for (var c = '๐'; c <= '๙'; c++) Assert.DoesNotContain(c, s);  // Thai digits take no marks
    }

    [Theory]
    [InlineData('ป', false)]
    [InlineData('า', false)]
    [InlineData('ำ', false)]
    [InlineData('เ', false)]
    [InlineData('ั', true)]
    [InlineData('๊', true)]
    [InlineData('ุ', true)]
    [InlineData('a', false)]
    public void IsMark_matches_the_Thai_combining_marks(char c, bool mark) => Assert.Equal(mark, ThaiMarkSeed.IsMark(c));

    [Theory]
    [InlineData("Stellar.Infrastructure.Resources.StellarUIThai-Regular.ttf")]
    [InlineData("Stellar.Infrastructure.Resources.StellarUIThaiBold.ttf")]
    public void Shipped_face_has_plain_Thai_mark_and_mkmk_lookups(string resource)
    {
        using var src = typeof(ThaiMarkSeed).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(src);
        using var ms = new MemoryStream();
        src!.CopyTo(ms);
        var types = ThaiGposLookupTypes(ms.ToArray());
        Assert.Contains("mark", types.Keys);
        Assert.Contains("mkmk", types.Keys);
        Assert.All(types["mark"], t => Assert.Equal(4, t));   // MarkToBase
        Assert.All(types["mkmk"], t => Assert.Equal(6, t));   // MarkToMark — never 9 (Extension)
    }

    // Minimal big-endian OpenType reader: thai script → default LangSys → features → lookup types.
    private static Dictionary<string, List<int>> ThaiGposLookupTypes(byte[] f)
    {
        int U16(int o) => (f[o] << 8) | f[o + 1];
        int U32(int o) => (f[o] << 24) | (f[o + 1] << 16) | (f[o + 2] << 8) | f[o + 3];
        string Tag(int o) => new string(new[] { (char)f[o], (char)f[o + 1], (char)f[o + 2], (char)f[o + 3] });

        var gpos = -1;
        for (int i = 0, n = U16(4); i < n; i++)
            if (Tag(12 + 16 * i) == "GPOS") gpos = U32(12 + 16 * i + 8);
        Assert.True(gpos > 0, "font has no GPOS table");
        int scripts = gpos + U16(gpos + 4), features = gpos + U16(gpos + 6), lookups = gpos + U16(gpos + 8);

        var langSys = -1;
        for (int i = 0, n = U16(scripts); i < n; i++)
            if (Tag(scripts + 2 + 6 * i) == "thai")
            {
                var script = scripts + U16(scripts + 2 + 6 * i + 4);
                langSys = script + U16(script);
            }
        Assert.True(langSys > 0, "no thai script with a default LangSys");

        var result = new Dictionary<string, List<int>>();
        for (int i = 0, n = U16(langSys + 4); i < n; i++)
        {
            var fi = U16(langSys + 6 + 2 * i);
            var tag = Tag(features + 2 + 6 * fi);
            var feature = features + U16(features + 2 + 6 * fi + 4);
            if (!result.TryGetValue(tag, out var list)) result[tag] = list = new List<int>();
            for (int k = 0, m = U16(feature + 2); k < m; k++)
            {
                var li = U16(feature + 4 + 2 * k);
                list.Add(U16(lookups + U16(lookups + 2 + 2 * li)));
            }
        }
        return result;
    }
}
