using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;
using HitchProbe = Stellar.Abstractions.Diagnostics.HitchProbe;

namespace Stellar.Infrastructure.Game;

// The LAYOUT half of a Conditional prebuild (see WindowBuilder.Prebuild.cs for the whole design) + its state types.
// A built branch's units are re-activated a few per frame inside an off-screen, invisible STAGE so Unity lays out,
// meshes and rasterizes it in the background, then the branch is deactivated and put back in its slot.
//
// Why a stage and not "activate it in place under alpha 0":
//   • in place it would join its parent's layout (a second Fill tab splits the body height with the visible one);
//     ignoreLayout fixes that but uGUI's layout dirt still climbs every ILayoutGroup parent → the WHOLE window
//     re-lays-out every reveal frame (incl. the visible tab's uncached TMP sizes). The stage has no layout group, so a
//     unit's dirt stops at the branch;
//   • the interaction ticker hit-tests rects itself (hovers, right-clicks, drag slots — not the raycaster), so an
//     alpha-0 copy on top of the visible tab would take hovers. The stage sits StageOffset units to the right.
// Why the branch is deactivated again afterwards (not kept active, re-shown by alpha): keeping every tab active keeps
// their renderers in the canvas batch and their maskables in every RectMask2D clip pass each frame (ChatTools measured
// ~0.75 ms/frame for ~600 extra renderers, WindowBuilder-Patterns.md), and moving an ACTIVE branch back home dirties
// every graphic anyway (OnTransformParentChanged). Inactive = zero steady cost; the show re-activates from warm caches
// (glyphs in the atlas, cached text generators) — the cost of a later visit, not a first one.
internal sealed partial class WindowBuilder
{
    // Units (an element's root GameObject + its subtree) re-activated per frame. Their real cost lands in Unity's
    // canvas rebuild AFTER this Update, which the stopwatch can't see — the cap bounds it. A constant, tuned from the
    // log: [UiPerf] canvasRebuild while a "[Prebuild] … revealed" line is pending.
    private const int RevealUnitsPerFrame = 12;
    private const float StageOffset = 100000f;   // canvas units to the right of the slot: never under the pointer
    // A revealed branch waits at most this many applies (~10 Hz) for its budgeted bubble measures before it is put away.
    private const int MaxMeasureWaitApplies = 60;

    internal sealed class PrebuildState
    {
        public readonly Queue<CondBinding> Roots = new();          // Prebuild Conditionals, in build (tree) order
        public PrebuildBranch? Building;                             // being built now
        public readonly List<PrebuildBranch> Built = new();         // built, units hidden, waiting for an on-screen window
        public PrebuildBranch? Revealing;                            // in the stage, units being re-activated
        public int StartFrame;
    }

    internal sealed class PrebuildBranch
    {
        public readonly CondBinding Cond; public readonly bool Then; public readonly GameObject Go;   // Go = the toggled GameObject
        public readonly string Name, BuildLabel, RevealLabel, FinishLabel;   // stable [Hitch] bucket names
        public readonly SlicedWorkStack<BuildItem> Work = new();
        public readonly List<GameObject> Units = new();
        // Units inside a bubble, left ACTIVE by the build (their bubble measures them): the first layout skips them, only a
        // re-warm turns them off and on (.Prebuild.cs RecordUnits). Same wrapper instances as in Units.
        public readonly HashSet<GameObject> Soft = new(ReferenceEqualityComparer.Instance);
        // The units still to re-activate, in order: Units itself for the first layout; a KeepAlive re-warm swaps in the
        // subset it deactivated (KeepAliveRules.RewarmPlan). Revealed indexes this list.
        public List<GameObject> Reveal;
        public Action Finish = null!;   // installed as the Conditional's BuildThen/BuildElse while this branch is pending
        public int CondScan, Revealed, Items, Slices, RevealFrames, ApplyMark = -1, Rewarms;
        public double TotalMs, MaxSliceMs, FinishMs = -1;
        public bool Done, Staged, Started;
        public GameObject? Stage; public Transform? Home; public int HomeIndex;
        public Vector2 AnchorMin, AnchorMax, Pivot, Pos, Size;

        public PrebuildBranch(CondBinding cond, bool then, GameObject go, string name, int condScan)
        {
            Cond = cond; Then = then; Go = go; Name = name; CondScan = condScan; Reveal = Units;
            BuildLabel = "prebuild:" + name; RevealLabel = "prebuild-layout:" + name; FinishLabel = "prebuild-finish:" + name;
        }

        /// <summary>A unit the reveal turns on: not destroyed, and not a soft unit on the first layout (it is already
        /// active, or a binding hid it — e.g. a list slot past the end — which a reveal must never undo).</summary>
        public bool Revealable(GameObject? u) => u != null && (Rewarms > 0 || !Soft.Contains(u));

        public void NoteSlice(long ticks)
        {
            var ms = ticks * 1000.0 / Stopwatch.Frequency;
            Slices++; TotalMs += ms;
            if (ms > MaxSliceMs) MaxSliceMs = ms;
        }

        public string Summary(string how) =>
            $"[Prebuild] {Name}: {how} — built {Items} items in {Slices} slices ({TotalMs:0.0} ms, max slice {MaxSliceMs:0.0} ms), "
            + $"laid out {Revealed}/{Reveal.Count} units over {RevealFrames} frames"
            + (Rewarms > 0 ? $", re-warm #{Rewarms} of {Units.Count} units" : "")
            + (FinishMs >= 0 ? $", finished on show in {FinishMs:0.0} ms" : "");
    }

    // true = this frame went to layout (no building); false = nothing to lay out now (the build may use the frame).
    private bool TickReveal(WindowToken t, PrebuildState st, long deadline)
    {
        var br = st.Revealing;
        if (br == null)
        {
            if (st.Built.Count == 0) return false;
            br = st.Revealing = st.Built[0];
            st.Built.RemoveAt(0);
        }
        if (br.Go == null) { MarkDone(t, br, "destroyed"); return true; }
        if (br.Revealed < br.Reveal.Count) { RevealSlice(t, br, deadline); return true; }
        // Every unit active: wait for an apply to fill the texts (TextBinding skips inactive text) and for its bubbles'
        // budgeted background measures (BubbleBinding.MeasureOrDefer), then for the canvas update after the last apply
        // (end of that frame), then put the branch back home, inactive (a KeepAlive one: parked, active).
        if (!BackgroundWarmRules.RevealSettled(t.ApplyCount - br.ApplyMark, t.BackgroundMeasurePending, MaxMeasureWaitApplies)
            || Time.frameCount <= t.LastApplyFrame) return false;
        Unstage(br);
        MarkDone(t, br, br.Rewarms > 0 ? "rewarmed" : "prebuilt");
        return true;
    }

    private static void RevealSlice(WindowToken t, PrebuildBranch br, long deadline)
    {
        var hT = HitchProbe.Begin();
        if (!br.Staged) Stage(br);
        var allow = new FrameAllowance(deadline, RevealUnitsPerFrame);
        while (br.Revealed < br.Reveal.Count)
        {
            var u = br.Reveal[br.Revealed];
            if (!br.Revealable(u)) { br.Revealed++; continue; }   // costs nothing: doesn't spend the frame's allowance
            if (!allow.TryTake(Stopwatch.GetTimestamp())) break;
            br.Revealed++;
            u.SetActive(true);
        }
        br.RevealFrames++;
        if (br.Revealed >= br.Reveal.Count) br.ApplyMark = t.ApplyCount;
        HitchProbe.End(br.RevealLabel, hT);
    }

    // Move the (inactive) branch into a fresh stage that covers its home slot, shifted off-screen, invisible and
    // click-through, then activate it — cheap: every unit under it is still inactive. A KeepAlive branch needs no stage:
    // its own parked placement already is one (off-screen, canvas off, its own layout root) — and it must never be
    // reparented, which would re-dirty every graphic in it.
    private static void Stage(PrebuildBranch br)
    {
        if (br.Cond.Keep is { } keep) { keep.BeginWarm(); br.Staged = true; return; }
        var tr = br.Go.transform;
        var rt = br.Go.GetComponent<RectTransform>();
        br.Home = tr.parent; br.HomeIndex = tr.GetSiblingIndex();
        br.AnchorMin = rt.anchorMin; br.AnchorMax = rt.anchorMax; br.Pivot = rt.pivot;
        br.Pos = rt.anchoredPosition; br.Size = rt.sizeDelta;
        var stage = UGuiPrimitives.NewChild("PrebuildStage", br.Home);
        stage.AddComponent<LayoutElement>().ignoreLayout = true;   // the home layout group never sizes or counts it
        var srt = stage.GetComponent<RectTransform>();
        srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;   // same size as the home slot → same wrap widths
        srt.offsetMin = new Vector2(StageOffset, 0f); srt.offsetMax = new Vector2(StageOffset, 0f);
        var cg = stage.AddComponent<CanvasGroup>();
        cg.alpha = 0f; cg.blocksRaycasts = false; cg.interactable = false;
        tr.SetParent(stage.transform, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        br.Stage = stage; br.Staged = true;
        br.Go.SetActive(true);
    }

    // Deactivate first (an inactive move dirties nothing), then back to its sibling slot with its old rect values —
    // the home layout group re-sizes it on its next show anyway.
    private static void Unstage(PrebuildBranch br)
    {
        if (br.Cond.Keep is { } keep) { keep.EndWarm(); br.Staged = false; return; }   // stays active, parked
        if (br.Go != null && br.Home != null)
        {
            br.Go.SetActive(false);
            var tr = br.Go.transform;
            tr.SetParent(br.Home, false);
            tr.SetSiblingIndex(Math.Min(br.HomeIndex, br.Home.childCount - 1));
            var rt = br.Go.GetComponent<RectTransform>();
            rt.anchorMin = br.AnchorMin; rt.anchorMax = br.AnchorMax; rt.pivot = br.Pivot;
            rt.anchoredPosition = br.Pos; rt.sizeDelta = br.Size;
        }
        if (br.Stage != null) UnityEngine.Object.Destroy(br.Stage);
        br.Stage = null; br.Staged = false;
    }
}
