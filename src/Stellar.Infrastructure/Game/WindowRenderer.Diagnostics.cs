using System.Text;
using Stellar.Abstractions.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// In-world layout diagnostics for the window renderer — gated on <see cref="StellarDiagnostics.IsEnabled"/>
/// (STELLAR_DIAGNOSTICS=1), zero cost otherwise. Dumps the actual RENDERED rect widths of a freshly-mounted
/// window's hierarchy plus each Text's preferred-vs-allotted width, so the recurring "text clips at the
/// RectMask2D edge in-world but the sandbox renders clean" bug can be MEASURED rather than guessed at (the
/// sandbox uses a different font, so it is unreliable for this — the project UI-verification rule). Look for the offender:
/// a Text whose <c>pref</c> exceeds its <c>w</c> while <c>flexW &lt; 0</c> (cannot shrink) is the overflow.
/// </summary>
internal sealed partial class WindowRenderer
{
    private void DumpRects(WindowBuilder.WindowToken token, string id)
    {
        if (!StellarDiagnostics.IsEnabled || token.Root == null) return;
        try
        {
            // uGUI lays out at end-of-frame; force it now so the rects we read are the real laid-out values.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(token.Rect);
            var sb = new StringBuilder();
            sb.Append("[Window/Diag] rect dump '").Append(id).Append("' (font=")
              .Append(_assets.MenuFont != null ? _assets.MenuFont.name : "NULL→builtin").Append("):\n");
            Walk(token.Rect, 0, sb);
            _log.Info(sb.ToString());
        }
        catch (System.Exception ex) { _log.Warning($"[Window/Diag] rect dump '{id}' threw: {ex.Message}"); }
    }

    // Recursive width dump. Caps depth + node count so a large panel can't flood the log.
    private static int _dumped;
    private void Walk(RectTransform rt, int depth, StringBuilder sb)
    {
        if (rt == null || depth > 8 || _dumped > 200) return;
        if (depth == 0) _dumped = 0;
        _dumped++;

        var w = rt.rect.width;
        sb.Append(' ', depth * 2).Append(rt.gameObject.name).Append("  w=").Append(w.ToString("0.0"));
        if (rt.GetComponent<Text>() is { } txt)
        {
            sb.Append("  textPref=").Append(txt.preferredWidth.ToString("0.0"))
              .Append(" overflow=").Append(txt.horizontalOverflow)
              .Append(" clipped=").Append(txt.preferredWidth > w + 0.5f ? "YES" : "no")
              .Append(" \"").Append(Trunc(txt.text)).Append('"');
        }
        if (rt.GetComponent<LayoutElement>() is { } le)
            sb.Append("  [LE flexW=").Append(le.flexibleWidth.ToString("0.##")).Append(" minW=").Append(le.minWidth.ToString("0.##")).Append(']');
        if (rt.GetComponent<RectMask2D>() != null) sb.Append("  <MASK>");
        sb.Append('\n');

        for (var i = 0; i < rt.childCount; i++)
            if (rt.GetChild(i) is RectTransform c) Walk(c, depth + 1, sb);
    }

    private static string Trunc(string? s)
        => string.IsNullOrEmpty(s) ? "" : s!.Length <= 24 ? s : s.Substring(0, 24) + "…";
}
