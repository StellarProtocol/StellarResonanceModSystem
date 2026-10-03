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

    private readonly IGameTypeRegistry _types;
    private readonly Func<IReadOnlyList<GameObject>> _overlayRoots;
    private readonly IPluginLog _log;
    private readonly GameEffectVisibility? _effects;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly EntityShowPlan _entityShow = new();
    private VisibilityLayers _applied;
    // Layers whose restore (show) call failed because the target singleton was briefly unavailable. Reassert
    // (and, transitively, TargetRebuilt via the host's drain) retries these even though nothing is held, so a
    // restore never silently strands the layer hidden — see LayerStepDecision.
    private VisibilityLayers _restorePending;

    public GameVisibilityBackend(IGameTypeRegistry types, Func<IReadOnlyList<GameObject>> overlayRoots, IPluginLog log, GameEffectVisibility? effects = null)
    {
        _types = types;
        _overlayRoots = overlayRoots;
        _log = log;
        _effects = effects;
    }

    public VisibilityLayers Apply(VisibilityLayers requested)
    {
        Step(VisibilityLayers.GameHud, requested, SetGameHudHidden, force: false);
        Step(VisibilityLayers.StellarOverlay, requested, SetOverlayHidden, force: false);
        Step(VisibilityLayers.Nameplates, requested, SetNameplatesHidden, force: false);
        StepOtherPlayers(requested);
        StepSelf(requested);
        var fx = _effects?.Apply(requested) ?? VisibilityLayers.None;
        _applied = (_applied & ~VisibilityLayerSets.Effects) | fx;
        return _applied;
    }

    /// <summary>Re-issues every held layer's game call (the game's own photo mode / cutscene / a rebuilt target may
    /// have undone it), AND retries any layer whose earlier restore (show) call failed because its singleton was
    /// briefly unavailable — even though that failure already cleared the layer from <c>_applied</c>, so nothing
    /// looks "held" for it. This is the path <c>TargetRebuilt</c> drives (via the host's one-tick-later drain),
    /// which is exactly when a previously-missing singleton is likely to have appeared. Other players and Self go
    /// through the entity-show plan, which rewrites only what differs.</summary>
    public VisibilityLayers Reassert(VisibilityLayers requested)
    {
        Step(VisibilityLayers.GameHud, requested, SetGameHudHidden, force: true);
        Step(VisibilityLayers.StellarOverlay, requested, SetOverlayHidden, force: true);
        Step(VisibilityLayers.Nameplates, requested, SetNameplatesHidden, force: true);
        StepOtherPlayers(requested);
        StepSelf(requested);
        var fx = _effects?.Apply(requested) ?? VisibilityLayers.None;
        _applied = (_applied & ~VisibilityLayerSets.Effects) | fx;
        return _applied;
    }

    private void Step(VisibilityLayers layer, VisibilityLayers requested, Func<bool, bool> setter, bool force)
    {
        var want = (requested & layer) != 0;
        var have = (_applied & layer) != 0;
        var restorePending = (_restorePending & layer) != 0;
        if (!LayerStepDecision.ShouldInvoke(want, have, force, restorePending)) return;
        var ok = Invoke(layer, want, () => setter(want));
        if (!want) _applied &= ~layer;         // restore attempted — never report a layer we tried to show
        else if (ok) _applied |= layer;
        else if (!force) _applied &= ~layer;   // a failed re-assert keeps the last known state
        _restorePending = LayerStepDecision.NextRestorePending(want, ok, restorePending)
            ? _restorePending | layer
            : _restorePending & ~layer;
        OnLayerSet(layer, want, ok);           // after the update, so a successful hide logs its applied bit
    }

    private void StepOtherPlayers(VisibilityLayers requested)
    {
        const VisibilityLayers both = VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty;
        var want = (requested & VisibilityLayers.OtherPlayers) != 0;
        var keep = want && (requested & VisibilityLayers.KeepParty) != 0;
        var target = want ? VisibilityLayers.OtherPlayers | (keep ? VisibilityLayers.KeepParty : 0) : VisibilityLayers.None;
        var wrote = _entityShow.NeedsWrite(want, keep, HoldCount);
        var ok = !wrote || Invoke(VisibilityLayers.OtherPlayers, want, () => SetOtherPlayersHidden(want, keep));
        _applied = (_applied & ~both) | (want && !ok ? VisibilityLayers.None : target);
        if (wrote) OnLayerSet(VisibilityLayers.OtherPlayers, want, ok);
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
