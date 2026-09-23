using System;

namespace Stellar.Infrastructure.UI;

/// <summary>Numeric-aware ("natural") string comparison for settings lists: a run of ASCII digits in
/// BOTH strings compares as an integer (leading zeros ignored) so <c>"apply.10"</c> sorts AFTER
/// <c>"apply.9"</c> instead of between <c>.1</c> and <c>.2</c>; every other character compares ordinally.
/// Pure + BCL-only so it is unit-tested without the uGUI panel it serves (HotkeysPanel).</summary>
internal static class NaturalOrder
{
    public static int Compare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            bool da = a[i] is >= '0' and <= '9', db = b[j] is >= '0' and <= '9';
            if (da && db)
            {
                int si = i, sj = j;
                while (i < a.Length && a[i] is >= '0' and <= '9') i++;
                while (j < b.Length && b[j] is >= '0' and <= '9') j++;
                var ra = a.AsSpan(si, i - si).TrimStart('0');
                var rb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (ra.Length != rb.Length) return ra.Length - rb.Length;   // more significant digits = larger
                var c = ra.SequenceCompareTo(rb);
                if (c != 0) return c;
                if (i - si != j - sj) return (i - si) - (j - sj);           // equal value: fewer leading zeros first
            }
            else
            {
                if (a[i] != b[j]) return a[i] - b[j];
                i++;
                j++;
            }
        }
        return (a.Length - i) - (b.Length - j);   // shorter remainder sorts first
    }
}
