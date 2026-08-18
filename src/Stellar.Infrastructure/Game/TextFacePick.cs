namespace Stellar.Infrastructure.Game;

/// <summary>Which shipped/system face family a string renders from (see <see cref="TmpFontAssets"/>).
/// Pure BCL — unit-tested in Stellar.Application.Tests; the rule Stage A inlined in TmpStyledText,
/// extracted and pinned.</summary>
internal enum FaceScript
{
    /// <summary>The shipped merged Latin+Thai face (also the default for null/empty).</summary>
    LatinThai,
    /// <summary>A system CJK family (ideographs, kana, Hangul — anything Thai-free with bold-risk glyphs).</summary>
    Cjk,
}

/// <summary>Face selection for styled overlay text: Thai (even mixed with Latin) and pure Latin render
/// from the merged Latin+Thai face; CJK/kana/Hangul render from the system CJK family.</summary>
internal static class TextFacePick
{
    public static FaceScript For(string? s)
        => GlyphScript.HasSyntheticBoldRisk(s) && !GlyphScript.IsThai(s) ? FaceScript.Cjk : FaceScript.LatinThai;
}
