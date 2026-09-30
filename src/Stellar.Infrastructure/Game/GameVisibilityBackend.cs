using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
using UnityEngine.UI;
namespace Stellar.Infrastructure.Game;

// StellarDiagnostics-gated per-layer logging lives in GameVisibilityBackend.Diagnostics.cs.

/// <summary>
/// Game-side hide switches, one per <see cref="VisibilityLayers"/> layer, each a transcription of a row in
/// docs/recon/photo-studio-render-recon.md. A layer's game call is made ONLY when that layer's wanted state changes
/// from what this backend last applied, so an untouched layer never overrides the game's own use of the same switch
/// (e.g. its camera mode hiding nameplates). Every game member is resolved by name at runtime (CI builds against
/// the refs/ stubs); a miss or throw fails open — one warning, the layer is reported not hidden, and the next
/// request retries (hot-update types may not be loaded yet at the title screen). Main thread only.
/// </summary>
internal sealed partial class GameVisibilityBackend : IVisibilityBackend
{
    private const string Tag = "[PhotoStudio] ";
    private const string ZUiRootType = "Panda.ZUi.ZUiRoot";
    private const string HudMgrType = "Panda.Hud.HudMgr";
    private const string CameraFrameCtrlType = "Panda.ZGame.CameraFrameCtrl";
    // E.CameraSystemShowEntityType (lua/common/enum_define.lua): Team = 3, OtherPlayer = 11.
    private const int EntityTypeTeam = 3;
    private const int EntityTypeOtherPlayer = 11;

    private readonly IGameTypeRegistry _types;
    private readonly Func<IReadOnlyList<GameObject>> _overlayRoots;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private VisibilityLayers _applied;

    public GameVisibilityBackend(IGameTypeRegistry types, Func<IReadOnlyList<GameObject>> overlayRoots, IPluginLog log)
    {
        _types = types;
        _overlayRoots = overlayRoots;
        _log = log;
    }

    public VisibilityLayers Apply(VisibilityLayers requested)
    {
        Step(VisibilityLayers.GameHud, requested, SetGameHudHidden);
        Step(VisibilityLayers.StellarOverlay, requested, SetOverlayHidden);
        Step(VisibilityLayers.Nameplates, requested, SetNameplatesHidden);
        StepOtherPlayers(requested);
        return _applied;
    }

    private void Step(VisibilityLayers layer, VisibilityLayers requested, Func<bool, bool> setter)
    {
        var want = (requested & layer) != 0;
        if (want == ((_applied & layer) != 0)) return;
        var ok = Invoke(layer, want, () => setter(want));
        if (!want) _applied &= ~layer;         // restore attempted — never report a layer we tried to show
        else if (ok) _applied |= layer;
    }

    private void StepOtherPlayers(VisibilityLayers requested)
    {
        const VisibilityLayers both = VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty;
        var want = (requested & VisibilityLayers.OtherPlayers) != 0;
        var keep = want && (requested & VisibilityLayers.KeepParty) != 0;
        var target = want ? VisibilityLayers.OtherPlayers | (keep ? VisibilityLayers.KeepParty : 0) : VisibilityLayers.None;
        if ((_applied & both) == target) return;
        var ok = Invoke(VisibilityLayers.OtherPlayers, want, () => SetOtherPlayersHidden(want, keep));
        _applied = (_applied & ~both) | (want && !ok ? VisibilityLayers.None : target);
    }

    private bool Invoke(VisibilityLayers layer, bool hide, Func<bool> call)
    {
        bool ok;
        try { ok = call(); }
        catch (Exception ex)
        {
            WarnOnce("throw:" + layer, $"Hide {layer} failed: {(ex.InnerException ?? ex).Message}");
            ok = false;
        }
        OnLayerSet(layer, hide, ok);
        return ok;
    }

    /// <summary>The framework's own canvases (window + layout-edit); the toast canvas stays visible by design.
    /// Canvas + raycaster are disabled rather than the GameObject deactivated, so injected components on the
    /// window canvas (WindowInteractionTicker) keep running and nothing re-mounts.</summary>
    private bool SetOverlayHidden(bool hidden)
    {
        var any = false;
        foreach (var root in _overlayRoots())
        {
            if (root == null) continue;
            var canvas = root.GetComponent<Canvas>();
            if (canvas != null) { canvas.enabled = !hidden; any = true; }
            var raycaster = root.GetComponent<GraphicRaycaster>();
            if (raycaster != null) raycaster.enabled = !hidden;
        }
        return any;
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }
}
