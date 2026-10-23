using System;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaProfileCardActionInjector"/>. Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state per-tick card poll pays zero log cost;
/// flip it on to confirm the action buttons injected into the native card and to trace the
/// resolved charId on click.
/// </summary>
internal sealed partial class PandaProfileCardActionInjector
{
    private bool _diagInjectedLogged;
    private bool _diagTemplateLogged;
    private bool _diagDividerLogged;

    // One-shot per process: the native sibling icon/label values we mirrored onto our button, so the
    // in-world tint + size can be confirmed against what got read.
    private void DiagNativeTemplate(NodeStyle iconStyle, NodeStyle labelStyle)
    {
        if (!StellarDiagnostics.IsEnabled || _diagTemplateLogged) return;
        _diagTemplateLogged = true;
        var iconDesc = iconStyle.Found ? $"size={iconStyle.SizeDelta} color={iconStyle.Color}" : "<not found, fallback>";
        var labelDesc = labelStyle.Found ? $"size={labelStyle.SizeDelta} color={labelStyle.Color}" : "<not found, fallback>";
        _log.Info($"[ProfileCardAction] native icon ref: {iconDesc} label-{labelDesc}");
    }

    // One-shot per process: the native action ITEM's full child hierarchy (name, Graphic kind, rect size,
    // active) + which node was chosen as the divider, so the real prefab node name can go in the KB. Also
    // flags nodes whose names hint at a hover/selected highlight (log only — hover isn't mirrored yet).
    private void DiagDividerHierarchy(Transform item, Graphic? chosen)
    {
        if (!StellarDiagnostics.IsEnabled || _diagDividerLogged) return;
        _diagDividerLogged = true;
        var sb = new StringBuilder();
        AppendNode(sb, item, 0);
        var pick = chosen != null
            ? $"'{chosen.gameObject.name}' size={chosen.rectTransform.rect.size} active={chosen.gameObject.activeSelf}"
            : "<none, no divider>";
        _log.Info($"[IdCard] native item '{item.name}' hierarchy:{sb}\n[IdCard] divider chosen: {pick}");
    }

    // Depth-capped recursive dump of one node + its children into the hierarchy log line.
    private static void AppendNode(StringBuilder sb, Transform t, int depth)
    {
        if (depth > 6) return;
        var rt = t.TryCast<RectTransform>();
        var size = rt != null ? rt.rect.size.ToString() : "-";
        var hint = NameSuggestsHighlight(t.name) ? " [HIGHLIGHT?]" : "";
        sb.Append('\n').Append(' ', depth * 2).Append(t.name)
          .Append(" kind=").Append(GraphicKind(t)).Append(" size=").Append(size)
          .Append(" active=").Append(t.gameObject.activeSelf).Append(hint);
        for (var i = 0; i < t.childCount; i++) AppendNode(sb, t.GetChild(i), depth + 1);
    }

    // The Graphic flavour on a node (IL2CPP wrappers need TryCast — `is` checks are always false).
    private static string GraphicKind(Transform t)
    {
        var g = t.GetComponent<Graphic>();
        if (g == null) return "none";
        if (g.TryCast<TMP_Text>() != null) return "TMP_Text";
        if (g.TryCast<Image>() != null) return "Image";
        if (g.TryCast<RawImage>() != null) return "RawImage";
        return "Graphic";
    }

    // "select"/"hover"/"press"/"light" anywhere; "on" only as a whole "_"-token (a bare substring would
    // match "icon"/"button").
    private static bool NameSuggestsHighlight(string name)
    {
        foreach (var hint in new[] { "select", "hover", "press", "light" })
            if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        foreach (var token in name.Split('_'))
            if (string.Equals(token, "on", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // One-shot per process: the from-scratch action buttons built into the native card's action bar.
    private void DiagInjected(int count)
    {
        if (!StellarDiagnostics.IsEnabled || _diagInjectedLogged) return;
        _diagInjectedLogged = true;
        _log.Info($"[IdCard] {count} action button(s) built into native profile card action bar");
    }

    // Per-click: the manual hit-test fired for an action and resolved the carded charId.
    private void DiagHitTestClick(string actionId, long charId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[IdCard] action '{actionId}' hit-test click -> charId={charId}");
    }

    // One-shot per process: the Lua bridge (mainState/DoString/LuaGetGlobal/LuaToInteger/LuaPop) resolved,
    // so click-time charId reads are live. Useful to confirm the bridge bound; not a steady-state event.
    private void DiagLuaResolved()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info("[IdCard] resolved: Z.UIMgr:GetView('idcard').cardId_ via LuaState.DoString + LuaToInteger");
    }

    // Click landed but the open-card cardId read came back 0 (idcard view absent or cardId_ unset).
    private void DiagCardIdReadFailed()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Warning("[IdCard] open-card cardId read came back 0");
    }
}
