using Stellar.Abstractions.Diagnostics;

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
