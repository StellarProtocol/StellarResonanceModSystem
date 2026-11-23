using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// StackLayer.OnEscape (framework 2.35.0): Esc closes a plugin's own in-window overlay (a popover drawn as a LayerStack
// layer), the way ConfirmHostElement's Esc cancels a confirm. Polled per render frame like the confirm and dismiss paths —
// the window's 10 Hz apply would miss a one-frame key edge.
//
// One press reaches at most ONE overlay (LayerStackRules.PickEscapeLayer): the frontmost shown window with an eligible
// overlay, then its top overlay (registration order = build order = draw order). Things that take the press first:
//  • a focused Stellar text field (sampled BEFORE the fields tick, as the sticky cursor does: that Esc defocuses it);
//  • an open dropdown / click-away pop-up (TickDismissables closes it on Esc — this runs first so it still sees it open);
//  • an open, on-screen confirm in the same window or a window in front (TickConfirmHosts cancels it — runs after this).
// Eligible = the layer is active in the hierarchy (window shown, layer shown by its binding), its window is not a
// soft-parked / background-warmed root (active but off-screen — BackgroundRoots), and its Visible() still says shown
// (the binding applies at 10 Hz, so a just-closed layer can still be active for a moment).
// Not swallowed: like the confirm's Esc, the press still reaches the game (the framework only hides Esc from the game
// while the sticky cursor holds it — HotkeyKeyBlockPatch.SetEscapeSwallow).
//
// Cost: zero with no OnEscape layer registered (one Count check). Otherwise one Input.GetKeyDown per frame; the walk
// runs only on an Esc press and reuses its lists (no allocation).
public sealed partial class WindowInteractionTicker
{
    internal sealed class EscapeLayerReg
    {
        public RectTransform Layer = null!;
        public Func<bool>? Visible;
        public Action OnEscape = null!;
        public long Seq;
    }

    internal readonly List<EscapeLayerReg> EscapeLayers = new();
    private readonly List<(int WindowZ, long Seq)> _escCandidates = new();
    private readonly List<EscapeLayerReg> _escCandidateRegs = new();
    private long _escSeq;

    /// <summary>An overlay with StackLayer.OnEscape. Called by WindowBuilder.BuildLayerStack in build order, right before
    /// the overlay's content is built, so a nested stack inside it registers after it (= drawn above it).</summary>
    internal void RegisterEscapeLayer(RectTransform layer, Func<bool>? visible, Action onEscape)
    {
        if (layer == null || onEscape == null) return;
        EscapeLayers.Add(new EscapeLayerReg { Layer = layer, Visible = visible, OnEscape = onEscape, Seq = ++_escSeq });
    }

    /// <summary>Sampled at the top of the frame, before the fields tick: an Esc this frame that no focused field owns.
    /// False without reading input when no OnEscape layer exists.</summary>
    private bool EscapeForLayers()
    {
        if (EscapeLayers.Count == 0 || !Input.GetKeyDown(KeyCode.Escape)) return false;
        return !AnyFieldFocused();
    }

    private void TickEscapeLayers(bool esc)
    {
        if (!esc || EscapeLayers.Count == 0) return;
        for (var i = 0; i < Dismissables.Count; i++)
        {
            var root = Dismissables[i].Root;
            if (root != null && root.gameObject.activeInHierarchy) return;   // the open pop-up takes this Esc
        }
        _escCandidates.Clear(); _escCandidateRegs.Clear();
        for (var i = 0; i < EscapeLayers.Count; i++)
        {
            var reg = EscapeLayers[i];
            if (reg.Layer == null || !reg.Layer.gameObject.activeInHierarchy) continue;
            var win = FindWindowRoot(reg.Layer);
            if (win == null || IsBackgroundRoot(win)) continue;
            try { if (reg.Visible != null && !reg.Visible()) continue; }
            catch { continue; }   // a throwing Visible: its binding hides the layer the same way
            _escCandidates.Add((win.GetSiblingIndex(), reg.Seq));
            _escCandidateRegs.Add(reg);
        }
        if (_escCandidates.Count == 0) return;
        var pick = LayerStackRules.PickEscapeLayer(_escCandidates, FrontConfirmZ());
        var chosen = pick >= 0 ? _escCandidateRegs[pick] : null;
        _escCandidateRegs.Clear();   // don't keep dead layers alive until the next press
        if (chosen == null) return;
        try { chosen.OnEscape(); }
        catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] layer OnEscape threw (rate-limited)"); }
    }

    // The highest window z with an OPEN confirm whose host is on screen (it cancels on this same Esc), else MinValue.
    private int FrontConfirmZ()
    {
        var z = int.MinValue;
        for (var i = 0; i < ConfirmHosts.Count; i++)
        {
            var reg = ConfirmHosts[i];
            if (reg.Ctl?.Current == null || reg.Root == null || !reg.Root.activeInHierarchy) continue;
            var win = FindWindowRoot(reg.Root.GetComponent<RectTransform>());
            if (win == null || IsBackgroundRoot(win)) continue;
            var s = win.GetSiblingIndex();
            if (s > z) z = s;
        }
        return z;
    }

    // Pointer compare: interop wrappers for the same native RectTransform aren't guaranteed to be the same object.
    private bool IsBackgroundRoot(RectTransform root)
    {
        var p = root.Pointer;
        for (var i = 0; i < BackgroundRoots.Count; i++)
            if (BackgroundRoots[i] != null && BackgroundRoots[i].Pointer == p) return true;
        return false;
    }

    private void PruneEscapeLayers()
    {
        for (var i = EscapeLayers.Count - 1; i >= 0; i--) if (EscapeLayers[i].Layer == null) EscapeLayers.RemoveAt(i);
    }
}
