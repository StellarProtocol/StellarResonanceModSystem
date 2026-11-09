namespace Stellar.Infrastructure.Game;

/// <summary>
/// The Thai characters whose GPOS mark-to-base / mark-to-mark records must be imported TOGETHER into a shipped TMP
/// face (see <c>TmpFontAssets.PrepareThai</c>). This game's TMP asks the native FontEngine for mark records ONLY among
/// the glyphs of one newly-added batch (<c>UpdateDiacriticalMarkAdjustmentRecords</c> passes just the newly-added
/// glyph list). So a base consonant rasterized in an earlier batch never gets a record for a mark added later, and
/// the mark then draws at its unpositioned default height ("ปลั๊ก": ั and ๊ side by side instead of stacked).
/// Seeding every consonant, vowel and mark in ONE batch, before any Thai glyph reaches the face, makes the record
/// set complete. Pure BCL, unit-tested.
/// </summary>
internal static class ThaiMarkSeed
{
    /// <summary>U+0E01–U+0E3A (consonants, ฤ ฦ, the vowels incl. above/below marks, phinthu) and U+0E40–U+0E4E
    /// (leading vowels, maiyamok, tone marks, thanthakhat, nikhahit, yamakkan): every base that a mark attaches to,
    /// and every mark. Digits and punctuation (U+0E3F, U+0E4F–U+0E5B) take no marks and stay on-demand.</summary>
    public static readonly string Characters = Build();

    /// <summary>True for a Thai combining mark (zero-advance, positioned by GPOS): mai han-akat, the above/below
    /// vowels, phinthu, maitaikhu, the four tone marks, thanthakhat, nikhahit, yamakkan.</summary>
    public static bool IsMark(char c) =>
        c == 'ั' || (c >= 'ิ' && c <= 'ฺ') || (c >= '็' && c <= '๎');

    private static string Build()
    {
        var chars = new char[(0x0E3A - 0x0E01 + 1) + (0x0E4E - 0x0E40 + 1)];
        var n = 0;
        for (var c = 0x0E01; c <= 0x0E3A; c++) chars[n++] = (char)c;
        for (var c = 0x0E40; c <= 0x0E4E; c++) chars[n++] = (char)c;
        return new string(chars, 0, n);
    }
}
