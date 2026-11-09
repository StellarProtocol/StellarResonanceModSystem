using System;
using System.Collections.Generic;
using System.Text;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TextElement.MaxLines line breaking, shared by the TMP path (TmpStyledText.MaxLines.cs) and the plain-Text path
/// (UGuiPrimitives.CapLines). Pure: the caller passes a one-line width measure, so it is unit-tested without Unity.
///
/// Why not the engines' own wrap: both TMP and uGUI break a word that is wider than the line in the MIDDLE and carry the
/// rest to the next line, so a two-line launcher name read "CombatMete" / "r" (in-game 2026-11-09). Here a line breaks
/// only at a space or after a hyphen (and between CJK characters, which have no spaces); a single word wider than the
/// line is cut with the ellipsis ON ITS OWN LINE ("CombatMet…"), never split across lines. When the text needs more than
/// <c>maxLines</c> lines, the last kept line ends in the ellipsis (dropping its trailing words first, then cutting
/// characters). The result is the lines joined with '\n' — the caller turns the engine's own wrapping OFF so an exact
/// fit can't be re-wrapped by float noise.
///
/// Rich-text tags (<c>&lt;sprite name=…&gt;</c>, <c>&lt;b&gt;</c>) are never split, and a space inside a tag is not a
/// break; a surrogate pair is never split either.
/// </summary>
internal static class WordWrapCap
{
    private readonly record struct Word(string Text, string Sep);

    public static string Cap(string s, Func<string, float> width, float maxW, int maxLines, string ellipsis)
    {
        if (string.IsNullOrEmpty(s) || maxLines <= 0) return s ?? "";
        bool Fits(string x) => width(x) <= maxW;

        var lines = new List<string>();
        var cur = "";
        foreach (var w in Split(s))
        {
            if (cur.Length > 0)
            {
                var cand = cur + w.Sep + w.Text;
                if (Fits(cand)) { cur = cand; continue; }
                lines.Add(cur);
                cur = "";
            }
            if (Fits(w.Text)) cur = w.Text;
            else lines.Add(CutWord(w.Text, Fits, ellipsis));   // overlong word: cut on its own line
            if (lines.Count > maxLines) break;                  // enough to know it overflows
        }
        if (cur.Length > 0) lines.Add(cur);
        if (lines.Count <= maxLines) return string.Join("\n", lines);

        var last = EndWithEllipsis(lines[maxLines - 1], Fits, ellipsis);
        lines.RemoveRange(maxLines - 1, lines.Count - (maxLines - 1));
        if (last.Length > 0) lines.Add(last);
        return string.Join("\n", lines);
    }

    // The last kept line + ellipsis: drop trailing words until it fits; a single word left is cut by characters.
    private static string EndWithEllipsis(string line, Func<string, bool> fits, string ell)
    {
        if (line.EndsWith(ell, StringComparison.Ordinal)) return line;   // already an overlong-word cut
        while (true)
        {
            if (fits(line + ell)) return line + ell;
            var k = LastBreak(line);
            if (k <= 0) return CutWord(line, fits, ell);
            line = line.Substring(0, k).TrimEnd();
        }
    }

    // Longest prefix of a word + ellipsis that fits (binary search over the legal cut points). Nothing fits → the
    // ellipsis alone, or empty when even that is too wide.
    private static string CutWord(string word, Func<string, bool> fits, string ell)
    {
        var cuts = CutPoints(word);
        var best = fits(ell) ? ell : "";
        int lo = 0, hi = cuts.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var c = word.Substring(0, cuts[mid]).TrimEnd() + ell;
            if (fits(c)) { best = c; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    // Index after the last break opportunity in a built line (the start of its last word), or 0 when it is one word.
    private static int LastBreak(string line)
    {
        var last = 0;
        Scan(line, i => { if (i > 0 && i < line.Length) last = i; });
        return last;
    }

    // Character positions (1..len-1) a word may be cut at: not inside a tag, not between a surrogate pair.
    private static List<int> CutPoints(string word)
    {
        var cuts = new List<int>(word.Length);
        var inTag = false;
        for (var i = 0; i < word.Length; i++)
        {
            if (!inTag && word[i] == '<' && word.IndexOf('>', i + 1) > 0) inTag = true;
            else if (inTag && word[i] == '>') inTag = false;   // the position right after a tag is a legal cut
            var n = i + 1;
            if (inTag || n >= word.Length || char.IsHighSurrogate(word[i])) continue;
            cuts.Add(n);
        }
        return cuts;
    }

    // Words with the separator that joins each to the previous one on the same line (" " after a space, "" after a
    // hyphen or around a CJK character).
    private static List<Word> Split(string s)
    {
        var words = new List<Word>();
        var sb = new StringBuilder();
        var sep = "";
        void Flush(string nextSep)
        {
            if (sb.Length > 0) { words.Add(new Word(sb.ToString(), sep)); sb.Clear(); sep = nextSep; }
            else if (nextSep.Length > 0 && words.Count > 0) sep = nextSep;
        }
        var inTag = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!inTag && c == '<' && s.IndexOf('>', i + 1) > 0) inTag = true;
            if (inTag) { sb.Append(c); if (c == '>') inTag = false; continue; }
            if (char.IsWhiteSpace(c)) { Flush(" "); continue; }
            if (IsCjk(c)) { Flush(""); sb.Append(c); Flush(""); continue; }
            sb.Append(c);
            if (c == '-' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1])) Flush("");
        }
        Flush("");
        return words;
    }

    // Break opportunities inside an already-built line (a space, after a hyphen, around CJK) — tag-aware, like Split.
    private static void Scan(string s, Action<int> onBreak)
    {
        var inTag = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!inTag && c == '<' && s.IndexOf('>', i + 1) > 0) inTag = true;
            if (inTag) { if (c == '>') inTag = false; continue; }
            if (c == ' ') onBreak(i);
            else if (c == '-') onBreak(i + 1);
            else if (IsCjk(c)) onBreak(i);
        }
    }

    // Kana, CJK ideographs, full-width forms: lines may break between any two of them (no spaces in the script).
    private static bool IsCjk(char c)
        => (c >= '぀' && c <= 'ヿ') || (c >= '㐀' && c <= '鿿') || (c >= '豈' && c <= '﫿')
        || (c >= '＀' && c <= '￯') || (c >= '　' && c <= '〿');
}
