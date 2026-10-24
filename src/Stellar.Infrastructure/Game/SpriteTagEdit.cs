using System;
using System.Text.RegularExpressions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Atomic-tag editing for the rich-emoji text field (InputElement.RichEmoji): an edit that removes PART of a
/// <c>&lt;sprite=N&gt;</c> tag is widened to remove the WHOLE tag, so one Backspace/Delete over an emoji picture
/// deletes the picture rather than leaving a broken literal like <c>&lt;sprite=12</c>. Works by diffing the text
/// before/after the edit (common prefix + suffix), so it is independent of how the TMP build's own Backspace maps
/// the caret over rich text: when TMP already removes the whole tag the removed span covers it fully and this is a
/// no-op. Pure string logic (no Unity) — unit-tested; safe in the sandbox.
/// </summary>
internal static class SpriteTagEdit
{
    private static readonly Regex Tag = new(@"<sprite=\d+>", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="after"/> removed part of (not all of) a tag present in
    /// <paramref name="before"/>; then <paramref name="repaired"/> is the text with every partially-touched tag
    /// removed whole (any inserted text kept) and <paramref name="caret"/> the string index right after the edit.</summary>
    public static bool TryRepair(string before, string after, out string repaired, out int caret)
    {
        repaired = after; caret = -1;
        if (string.IsNullOrEmpty(before) || after == null || before.IndexOf("<sprite", StringComparison.Ordinal) < 0)
            return false;
        int max = Math.Min(before.Length, after.Length);
        int p = 0;
        while (p < max && before[p] == after[p]) p++;
        int s = 0;
        while (s < max - p && before[before.Length - 1 - s] == after[after.Length - 1 - s]) s++;
        int remStart = p, remEnd = before.Length - s;   // span of `before` the edit replaced
        if (remEnd <= remStart) return false;           // pure insertion — nothing removed
        int ns = remStart, ne = remEnd;
        for (var m = Tag.Match(before); m.Success; m = m.NextMatch())
        {
            int ts = m.Index, te = m.Index + m.Length;
            if (te <= remStart || ts >= remEnd) continue;   // untouched
            if (ts >= remStart && te <= remEnd) continue;   // already removed whole
            ns = Math.Min(ns, ts); ne = Math.Max(ne, te);
        }
        if (ns == remStart && ne == remEnd) return false;
        var inserted = after.Substring(p, after.Length - s - p);
        repaired = before.Substring(0, ns) + inserted + before.Substring(ne);
        caret = ns + inserted.Length;
        return true;
    }

    /// <summary>InputElement.InsertRequest: <paramref name="insert"/> replaces the selection
    /// [<paramref name="a"/>, <paramref name="b"/>) of <paramref name="text"/> (either order; a caret = a == b; clamped
    /// to the text; -1 = "no known caret" → append at the end). A bound that falls INSIDE a <c>&lt;sprite=N&gt;</c> tag
    /// is moved out of it — the start to the tag's start, the end to the tag's end — so a tag is never split.
    /// <paramref name="caret"/> = the string index right after the inserted text.</summary>
    public static string InsertAt(string text, int a, int b, string insert, out int caret)
    {
        text ??= string.Empty; insert ??= string.Empty;
        int lo, hi;
        if (a < 0 || b < 0) lo = hi = text.Length;
        else { lo = Math.Clamp(Math.Min(a, b), 0, text.Length); hi = Math.Clamp(Math.Max(a, b), 0, text.Length); }
        bool caretOnly = lo == hi;   // a plain caret inside a tag moves to its start (a selection is widened instead)
        for (var m = Tag.Match(text); m.Success; m = m.NextMatch())
        {
            int ts = m.Index, te = m.Index + m.Length;
            if (lo > ts && lo < te) lo = ts;
            if (hi > ts && hi < te) hi = caretOnly ? ts : te;
        }
        caret = lo + insert.Length;
        return text.Substring(0, lo) + insert + text.Substring(hi);
    }
}
