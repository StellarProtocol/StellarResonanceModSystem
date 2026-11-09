using System;
using System.Collections;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;
using HitchProbe = Stellar.Abstractions.Diagnostics.HitchProbe;

namespace Stellar.Infrastructure.Game;

// ConditionalElement.KeepAlive: a branch that, once built and shown (or pre-built), is PARKED when hidden instead of
// SetActive(false), so showing it again is ~a normal frame. Measured 11-09 on the Settings hub after the background
// pre-build: every tab switch still cost 45-65 ms — ~20-25 ms in the window apply (the Conditional flip counted as
// structural → whole-window re-layout, plus ~30-60 BubbleElement width re-measures: a bubble re-measures on every
// inactive→active edge) and ~15-27 ms in Unity's canvas rebuild (inactive→active = OnEnable on every graphic =
// re-mesh + re-layout of the whole tab).
//
// Structure: Cond container (the layout slot) → "KeepAlive" host → "Then" arm (stretched over the host) → content.
//  • The HOST is a layout BOUNDARY: no ILayoutElement / ILayoutGroup on it, so uGUI's layout dirt climbing out of the
//    branch stops at the arm, and a window re-layout never descends into it. The price: the branch takes its slot's
//    size and never reports its own size up — KeepAlive implies Fill, for a fixed-size slot (the Settings hub body).
//  • The host carries its OWN Canvas (+ GraphicRaycaster). Parked = canvas.enabled false: Unity keeps the branch's
//    meshes and batches and draws nothing; re-enabling re-batches only. No OnDisable/OnEnable on any graphic.
//    overridePixelPerfect: a hidden tab is never re-snapped by a pixelPerfect flip; while shown the ticker keeps it in
//    step with its window (WindowInteractionTicker.PixelPerfect.cs, "follower" canvases).
//  • Parked = also OUT of its column's layout (LayoutElement.ignoreLayout) and moved StageOffset units to the right:
//    the ticker hit-tests rects itself (hovers, right-clicks, drag slots, scroll views) and ignores canvas state, so an
//    invisible copy must not sit under the pointer. Its size stays the slot's, so un-parking changes no size → no
//    re-layout inside it; only the column re-places it.
//  • Every binding built inside is tagged with the branch's AliveGate (TagAlive) and skipped while parked — zero
//    binding work for hidden tabs; on show the bindings re-pull and only what changed while hidden is dirtied.
//
// A Prebuild + KeepAlive branch is also claimed at registration, so its units are known (.Prebuild.cs): the first
// background layout happens IN PLACE (BeginWarm = parked + units re-activated a few per frame; no stage reparent,
// which would re-dirty every graphic) and the branch stays parked instead of being deactivated. When the WINDOW is
// re-shown, its root re-activation would OnEnable every parked tab at once, so PrepareReshow (called while the root is
// still inactive) puts their units back off and re-warms them in the background (KeepAliveRules.OnWindowReshow).
//
// Steady cost while parked: no bindings, no draw; uGUI still runs each parked RectMask2D's clip pass and each ScrollRect's
// LateUpdate (cheap, nothing moves), and a font-atlas rebuild re-meshes parked text too (all enabled Text is tracked).
// Not covered inside a KeepAlive branch: InputElement.AutoFocus (would focus an off-screen field) and
// RowElement.ConstrainsWindowMinWidth (a parked row still counts) — neither is used by the Settings hub.
internal sealed partial class WindowBuilder
{
    private AliveGate? _alive;   // innermost KeepAlive branch being built (BuildScope.Alive)

    /// <summary>A KeepAlive host's own canvas (host rect, canvas, diagnostics id) → the ticker keeps its pixelPerfect in
    /// step with its window while shown. Null in the sandbox.</summary>
    internal Action<RectTransform, Canvas, string>? RegisterLocalCanvas { get; set; }

    private KeepAliveHost NewKeepAliveHost(GameObject container, ConditionalElement cond, WindowToken token)
    {
        // Fill is implied: the host reports no size, so the slot must hand the branch its height.
        var le = container.GetComponent<LayoutElement>();
        le.flexibleHeight = 1f;
        if (container.GetComponent<VerticalLayoutGroup>() is { } v) v.childForceExpandHeight = true;
        var hostGo = UGuiPrimitives.NewChild("KeepAlive", container.transform);   // no layout components: a boundary
        var canvas = hostGo.AddComponent<Canvas>();   // Canvas before the raycaster ([RequireComponent(Canvas)])
        canvas.overridePixelPerfect = true;
        canvas.pixelPerfect = true;
        var raycaster = hostGo.AddComponent<GraphicRaycaster>();
        var arm = NewCondArm("Then", hostGo, fill: true).GetComponent<RectTransform>();
        arm.anchorMin = Vector2.zero; arm.anchorMax = Vector2.one; arm.offsetMin = Vector2.zero; arm.offsetMax = Vector2.zero;
        var name = cond.PrebuildName ?? token.WindowId + ".keep" + token.KeepAlives.Count;
        var host = new KeepAliveHost
        {
            Gate = new AliveGate { Parent = _alive }, Container = container, ContainerRt = container.GetComponent<RectTransform>(),
            Le = le, Canvas = canvas, Raycaster = raycaster, Arm = arm, Token = token, Name = name, ShowLabel = "tabshow:" + name,
        };
        host.FinishWarm = h => FinishWarmNow(token, h);
        token.KeepAlives.Add(host);
        RegisterLocalCanvas?.Invoke(hostGo.GetComponent<RectTransform>(), canvas, token.WindowId + "/" + name);
        return host;
    }

    // Shown while its background re-warm is still running: re-activate the rest of its units now, end the warm.
    private void FinishWarmNow(WindowToken t, KeepAliveHost h)
    {
        if (h.Branch is not { } br) return;
        var hT = HitchProbe.Begin();
        RevealAll(br);
        if (br.Staged) Unstage(br);
        MarkDone(t, br, "shown");
        HitchProbe.End(br.FinishLabel, hT);
    }

    // The window root is about to re-activate (still inactive: SetActive below fires no callbacks). Put the branch's
    // active units back off and queue them for the background reveal. False = nothing to reveal (stays parked).
    private static bool QueueRewarm(WindowToken t, KeepAliveHost h)
    {
        var br = h.Branch!;
        var pending = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);   // an interrupted warm's leftovers
        for (var i = br.Revealed; i < br.Reveal.Count; i++) if (br.Reveal[i] != null) pending.Add(br.Reveal[i]);
        var plan = KeepAliveRules.RewarmPlan(br.Units, pending, u => u != null && u.activeSelf);
        if (plan.Count == 0) return false;
        for (var i = 0; i < plan.Count; i++) if (plan[i] != null && plan[i].activeSelf) plan[i].SetActive(false);
        br.Reveal = plan; br.Revealed = 0; br.ApplyMark = -1; br.RevealFrames = 0; br.Rewarms++; br.Done = false;
        br.Staged = true;   // its parked placement is its stage (Stage() would only re-park it)
        var st = t.Prebuild ??= new PrebuildState { StartFrame = Time.frameCount + PrebuildStartDelayFrames };
        if (st.Revealing != br && !st.Built.Contains(br)) st.Built.Add(br);
        return true;
    }

    // ---- binding gate tagging ----

    // Every per-apply binding list of a WindowToken (one source of truth for AliveMarks/TagAlive).
    private static readonly Func<WindowToken, IList>[] GatedLists =
    {
        t => t.VirtualLists, t => t.Conds, t => t.Lists, t => t.Texts, t => t.StyledTexts, t => t.Sprites, t => t.Icons,
        t => t.GameTextures, t => t.Buttons, t => t.Toggles, t => t.Sliders, t => t.XYPads, t => t.Swatches, t => t.Bars,
        t => t.Pickers, t => t.FrameOpacities, t => t.Hovers, t => t.Selectables, t => t.Bubbles, t => t.MeterRows,
        t => t.AccentRows, t => t.CooldownTiles, t => t.Charts, t => t.FieldSyncs,
    };

    // The token's binding-list counts before a build inside a KeepAlive branch (null = not inside one: nothing to tag).
    private static int[]? AliveMarks(WindowToken t, AliveGate? gate)
    {
        if (gate == null) return null;
        var m = new int[GatedLists.Length];
        for (var i = 0; i < m.Length; i++) m[i] = GatedLists[i](t).Count;
        return m;
    }

    // Gate every binding added since the marks. Innermost wins (??=): a nested KeepAlive branch is built by its own lazy
    // build and tags its range first; its CondBinding sits in the outer range and is gated by the outer branch.
    private static void TagAlive(WindowToken t, int[]? marks, AliveGate? gate)
    {
        if (marks == null || gate == null) return;
        for (var i = 0; i < marks.Length; i++)
        {
            var list = GatedLists[i](t);
            for (var k = marks[i]; k < list.Count; k++) if (list[k] is GatedBinding b) b.Alive ??= gate;
        }
    }

    // One KeepAlive branch's GameObjects + state. Transitions come from KeepAliveRules (pure, unit-tested).
    internal sealed class KeepAliveHost
    {
        private static readonly string[] KindNames = { "cold", "warming", "parked", "shown" };
        public AliveGate Gate = null!;
        public GameObject Container = null!;
        public RectTransform ContainerRt = null!;
        public LayoutElement Le = null!;
        public Canvas Canvas = null!;
        public GraphicRaycaster Raycaster = null!;
        public RectTransform Arm = null!;
        public WindowToken Token = null!;
        public string Name = "", ShowLabel = "";
        public KeepAliveState State;
        public PrebuildBranch? Branch;               // its prebuild record (units) — null without Prebuild
        public Action<KeepAliveHost>? FinishWarm;    // shown mid re-warm (WindowBuilder.FinishWarmNow)

        /// <summary>CondBinding flip. True only for a SetActive (cold show / cold hide) — the old structural case.</summary>
        public bool Apply(bool show)
        {
            var (next, act) = show ? KeepAliveRules.OnShow(State) : KeepAliveRules.OnHide(State);
            var hT = show ? HitchProbe.Begin() : 0L;
            var from = State;
            switch (act)
            {
                case KeepAliveAction.Activate: Container.SetActive(true); EnterShown(); break;   // first-show cost
                case KeepAliveAction.FinishWarm: FinishWarm?.Invoke(this); EnterShown(); break;
                case KeepAliveAction.Unpark: EnterShown(); break;
                case KeepAliveAction.Park: Park(); break;
                case KeepAliveAction.Deactivate: Container.SetActive(false); break;
            }
            State = next;
            if (hT != 0L) { HitchProbe.End(ShowLabel, hT); UiPerfStats.TabShow(Name, KindNames[(int)from], hT); }
            return act is KeepAliveAction.Activate or KeepAliveAction.Deactivate;
        }

        // Back into the column (which re-places it at the same size), drawn, hittable, bindings on.
        private void EnterShown()
        {
            Le.ignoreLayout = false;
            if (!Canvas.enabled) Canvas.enabled = true;
            if (!Raycaster.enabled) Raycaster.enabled = true;
            Gate.Shown = true; Gate.Warming = false;
        }

        private void Park()
        {
            Token.DefocusFieldsUnder(Container.transform);   // SetActive(false) used to end a focused field's edit
            Gate.Shown = false;
            Canvas.enabled = false; Raycaster.enabled = false;
            PlaceOffscreen();
        }

        // Out of the column's layout, stretched over the column (= the slot: a KeepAlive tab set is the column's only
        // content) and StageOffset units to the right — never under the pointer.
        private void PlaceOffscreen()
        {
            Le.ignoreLayout = true;
            ContainerRt.anchorMin = Vector2.zero; ContainerRt.anchorMax = Vector2.one;
            ContainerRt.offsetMin = new Vector2(StageOffset, 0f); ContainerRt.offsetMax = new Vector2(StageOffset, 0f);
        }

        /// <summary>Prebuild layout start (WindowBuilder.Stage): park in place, then activate — every unit is inactive.</summary>
        public void BeginWarm()
        {
            if (State == KeepAliveState.Shown) return;
            Gate.Shown = false; Gate.Warming = true;   // its bindings run so its texts are filled before it parks
            Canvas.enabled = false; Raycaster.enabled = false;
            PlaceOffscreen();
            if (!Container.activeSelf) Container.SetActive(true);
            State = KeepAliveState.Warming;
        }

        /// <summary>Prebuild layout done (WindowBuilder.Unstage): stays active and parked.</summary>
        public void EndWarm()
        {
            Gate.Warming = false;
            if (State == KeepAliveState.Warming) State = KeepAliveState.Parked;
        }
    }

    public sealed partial class WindowToken
    {
        internal readonly List<KeepAliveHost> KeepAlives = new();

        /// <summary>Called right before a hidden window's root is re-activated (WindowRenderer.ApplyValues), while it is
        /// still inactive: parked branches re-warm in the background (or go cold) instead of all re-enabling at once.</summary>
        internal void PrepareReshow()
        {
            for (var i = 0; i < KeepAlives.Count; i++)
            {
                var h = KeepAlives[i];
                var (next, act) = KeepAliveRules.OnWindowReshow(h.State, h.Branch is { Units.Count: > 0 });
                if (act == KeepAliveAction.Rewarm && !QueueRewarm(this, h)) next = KeepAliveState.Parked;
                else if (act == KeepAliveAction.Deactivate) h.Container.SetActive(false);
                h.State = next;
                h.Gate.Warming = next == KeepAliveState.Warming;
            }
        }

        // Reskin: each kept-alive branch is its own layout root, so the window's forced rebuild doesn't reach it.
        private void RelayoutKeepAlives()
        {
            for (var i = 0; i < KeepAlives.Count; i++)
            {
                var h = KeepAlives[i];
                if (h.State == KeepAliveState.Cold || h.Arm == null) continue;
                try
                {
                    if (h.State == KeepAliveState.Shown) LayoutRebuilder.ForceRebuildLayoutImmediate(h.Arm);
                    else LayoutRebuilder.MarkLayoutForRebuild(h.Arm);
                }
                catch { /* a destroyed arm: skip */ }
            }
        }

        internal void DefocusFieldsUnder(Transform root)
        {
            for (var i = 0; i < Fields.Count; i++) Fields[i].DefocusIfUnder(root);
        }
    }
}
