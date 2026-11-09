using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Services;
using UnityEngine;
using HitchProbe = Stellar.Abstractions.Diagnostics.HitchProbe;

namespace Stellar.Infrastructure.Game;

// ConditionalElement.Prebuild: build a Conditional's not-yet-shown branch(es) in the BACKGROUND after the window
// mounts, a few ms per rendered frame, so the first show of that branch only re-activates it. The Settings hub tabs
// (one Conditional per tab) cost 65-78 ms on their first visit (≈20-28 ms building the tab tree + 17-32 ms canvas
// rebuild/layout, measured 11-09) — the lazy branch build was the hitch.
//
// BUILD (any time after mount, also while the window is parked/hidden): the branch is built item by item from a
// SlicedWorkStack under a 3 ms per-frame budget shared by all windows (TickPrebuilds, pumped by the window ticker's
// per-frame hook). An item is one element: a SLICEABLE container (Row/Column/Cell/Panel/Scroll/List/uniform
// VirtualList/Conditional — whose builder does nothing after its children that depends on them) builds only its own
// GameObject and pushes its children (BuildChild); anything else builds atomically, nested content included (_slicer
// is null while it builds, so a Column inside a Bubble still builds inline). Every item restores the BuildScope its
// lazy build would have had, so bindings are exactly the lazy path's. A nested Conditional met on the way gets its
// CURRENT branch claimed and queued too (else the next apply would build it unsliced); list-slot (gated) ones stay lazy.
//
// LAYOUT (only while the window is on screen — a parked root is inactive, and Unity lays out nothing inactive): each
// item's new GameObjects were deactivated as "units". The built branch is moved into an off-screen, alpha-0,
// non-raycast STAGE (a non-layout parent, so a unit's layout dirt stops at the branch instead of climbing to the
// window root) the size of its home slot, and its units are re-activated a few per frame → Unity lays out + meshes
// (and rasterizes the glyphs of) a little each frame. Once an apply has filled the texts (TextBinding skips
// inactive text) and a canvas update has run, the branch is deactivated and put back home. Steady state = exactly
// today's after-visit state (hidden tabs inactive): no per-frame cost; the first show pays only the re-activation,
// like a later visit.
//
// SHOW BEFORE DONE: a claimed root's BuildThen/BuildElse is replaced by FinishBranch, which CondBinding.Apply calls
// right before activating it: it builds the rest synchronously, re-activates every unit and un-stages — a branch is
// never shown half-built. A root not reached yet still has its normal lazy build.
internal sealed partial class WindowBuilder
{
    private const double PrebuildBudgetMs = 3.0;
    private const int PrebuildStartDelayFrames = 2;   // let the mount frame (and its first-apply layout) pass first

    /// <summary>Diagnostics sink for the per-branch [Prebuild] summary (logged only with HITCH/diagnostics on).</summary>
    internal Action<string>? Log { get; set; }

    // The work stack of the branch whose SLICEABLE item is building right now; null = BuildChild builds inline (every
    // normal build, every atomic prebuild item, every lazy branch build).
    private SlicedWorkStack<BuildItem>? _slicer;

    internal readonly struct BuildItem
    {
        public readonly HudElement El; public readonly Transform Parent; public readonly BuildScope Scope;
        public readonly bool Owned;   // inside a bubble: one atomic BuildDeferred (owner tagging), never split
        public BuildItem(HudElement el, Transform parent, BuildScope scope, bool owned)
        { El = el; Parent = parent; Scope = scope; Owned = owned; }
    }

    /// <summary>What a Conditional branch's lazy build would build (CondBinding.ThenLazy/ElseLazy) — a prebuild claims it.</summary>
    internal sealed class LazyBranch
    {
        public readonly HudElement El; public readonly Transform Parent; public readonly BuildScope Scope;
        public LazyBranch(HudElement el, Transform parent, BuildScope scope) { El = el; Parent = parent; Scope = scope; }
    }

    // A child of a sliceable container: pushed onto the prebuild stack while one is slicing, else built now.
    private void BuildChild(HudElement child, Transform parent, WindowToken token)
    {
        if (_slicer != null) _slicer.Push(new BuildItem(child, parent, CaptureScope(), owned: false));
        else BuildElement(child, parent, token);
    }

    // Containers whose builder only builds its children (via BuildChild) and reads none of them afterwards.
    private static bool IsSliceable(HudElement el) =>
        el is RowElement or ColumnElement or CellElement or PanelElement or ScrollElement or ListElement or ConditionalElement
        || el is VirtualListElement { VariableHeight: false };

    // BuildConditional: a Prebuild Conditional outside a variable-list slot joins the window's prebuild queue.
    // A KeepAlive one is claimed NOW, so even the branch the first apply shows is built item by item (FinishBranch) and
    // records its units — what lets it be re-warmed in the background after the window was hidden (.KeepAlive.cs).
    private void RegisterPrebuild(WindowToken token, CondBinding c, string? name)
    {
        var st = token.Prebuild ??= new PrebuildState { StartFrame = Time.frameCount + PrebuildStartDelayFrames };
        c.PrebuildRoot = true;
        c.PrebuildName = name ?? token.WindowId + ".cond" + st.Roots.Count;
        st.Roots.Enqueue(c);
        if (c.Keep != null) c.Keep.Branch = ClaimRoot(token, c, then: true);
    }

    /// <summary>Per rendered frame (window ticker): advance every window's prebuild inside ONE shared budget.</summary>
    internal void TickPrebuilds(List<WindowToken> tokens)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(PrebuildBudgetMs * Stopwatch.Frequency / 1000.0);
        for (var i = 0; i < tokens.Count && Stopwatch.GetTimestamp() < deadline; i++)
        {
            var t = tokens[i];
            if (t.Prebuild == null) continue;
            if (t.Root == null) { t.Prebuild = null; continue; }   // window destroyed mid-job
            if (Time.frameCount < t.Prebuild.StartFrame) continue;
            // A throw drops only the background job; a claimed branch still finishes itself on show (FinishBranch).
            try { if (!TickPrebuild(t, deadline)) t.Prebuild = null; }   // false = every branch done
            catch (Exception ex) { t.Prebuild = null; Warn?.Invoke($"[Prebuild] {t.WindowId} stopped: {ex.Message}"); }
        }
    }

    // One window's share: lay out a built branch while on screen, else build. False = nothing left to do.
    private bool TickPrebuild(WindowToken t, long deadline)
    {
        var st = t.Prebuild!;
        if (t.Root.activeInHierarchy && TickReveal(t, st, deadline)) return true;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (st.Building == null && !StartNextBranch(t, st)) break;
            BuildSlice(t, st.Building!, deadline);
            if (st.Building is { Work.Count: 0 } done) CompleteBuild(t, st, done);
        }
        return st.Building != null || st.Roots.Count > 0 || st.Built.Count > 0 || st.Revealing != null;
    }

    // Next root branch still waiting for its lazy build (Then, then Else). One shown already is skipped.
    private bool StartNextBranch(WindowToken t, PrebuildState st)
    {
        while (st.Roots.Count > 0)
        {
            var c = st.Roots.Peek();
            // A KeepAlive root was claimed at registration; it still needs building unless shown (finished) meanwhile.
            var br = c.Keep?.Branch is { Started: false, Done: false } pre ? pre : ClaimRoot(t, c, then: true) ?? ClaimRoot(t, c, then: false);
            if (br != null) { StartBranch(t, br); st.Building = br; return true; }
            st.Roots.Dequeue();
        }
        return false;
    }

    // From here on, Conditionals registered on the token come from this branch's own build (ClaimNested scans them).
    private static void StartBranch(WindowToken t, PrebuildBranch br)
    {
        if (br.Started) return;
        br.Started = true;
        br.CondScan = t.Conds.Count;
    }

    private PrebuildBranch? ClaimRoot(WindowToken t, CondBinding c, bool then)
    {
        var lazy = then ? c.ThenLazy : c.ElseLazy;
        var go = then ? c.Then : c.Else;
        if (lazy == null || go == null || (then ? c.BuildThen : c.BuildElse) == null) return null;
        var br = new PrebuildBranch(c, then, go, c.PrebuildName + (then ? "" : ".else"), t.Conds.Count);
        br.Finish = () => FinishBranch(t, br);
        if (then) { c.ThenLazy = null; c.BuildThen = br.Finish; } else { c.ElseLazy = null; c.BuildElse = br.Finish; }
        br.Work.Push(new BuildItem(lazy.El, lazy.Parent, lazy.Scope, owned: lazy.Scope.BubbleOwner != null));
        return br;
    }

    private void BuildSlice(WindowToken t, PrebuildBranch br, long deadline)
    {
        var hT = HitchProbe.Begin();
        var t0 = Stopwatch.GetTimestamp();
        try { br.Items += br.Work.Run(item => BuildStep(t, br, item), Stopwatch.GetTimestamp, deadline); }
        catch (Exception ex) { AbortBranch(t, br, ex); }
        br.NoteSlice(Stopwatch.GetTimestamp() - t0);
        HitchProbe.End(br.BuildLabel, hT);
    }

    // One item: build it with its captured scope (sliceable = own GameObject only), hide its new GameObjects as reveal
    // units, then claim the current branch of every nested Conditional it registered.
    private void BuildStep(WindowToken t, PrebuildBranch br, BuildItem item)
    {
        if (item.Parent == null) return;   // parent destroyed under us
        var before = item.Parent.childCount;
        if (item.Owned) BuildDeferred(item.El, item.Parent, t, item.Scope);   // exactly the lazy path, owner tagging included
        else
        {
            var prev = CaptureScope();
            RestoreScope(item.Scope);
            var alive = AliveMarks(t, item.Scope.Alive);
            _slicer = IsSliceable(item.El) ? br.Work : null;
            try { BuildElement(item.El, item.Parent, t); }
            finally { _slicer = null; RestoreScope(prev); }
            TagAlive(t, alive, item.Scope.Alive);   // a KeepAlive branch's bindings are gated (.KeepAlive.cs)
        }
        // A Conditional's own GameObjects are toggled by its CondBinding (never a unit); a bubble's content is measured
        // by its BubbleBinding (left active).
        if (!item.Owned && item.El is not ConditionalElement) HideUnits(br, item.Parent, before);
        ClaimNested(t, br);
    }

    private static void HideUnits(PrebuildBranch br, Transform parent, int from)
    {
        for (var k = from; k < parent.childCount; k++)
        {
            var go = parent.GetChild(k).gameObject;
            if (!go.activeSelf) continue;   // built inactive on purpose (its binding shows it) — not ours to toggle
            go.SetActive(false);
            br.Units.Add(go);
        }
    }

    // Conditionals registered since the last scan: queue the branch their When picks NOW (the one the next apply would
    // build unsliced). Prebuild roots are queued on their own; list-slot (gated) ones stay lazy under the slot cap.
    private static void ClaimNested(WindowToken t, PrebuildBranch br)
    {
        for (; br.CondScan < t.Conds.Count; br.CondScan++)
        {
            var c = t.Conds[br.CondScan];
            if (c.PrebuildRoot || c.Gate != null) continue;
            bool show;
            try { show = c.When(); } catch { continue; }
            var lazy = show ? c.ThenLazy : c.ElseLazy;
            if (lazy == null || (show ? c.BuildThen : c.BuildElse) == null) continue;
            if (show) { c.BuildThen = null; c.ThenLazy = null; } else { c.BuildElse = null; c.ElseLazy = null; }
            br.Work.Push(new BuildItem(lazy.El, lazy.Parent, lazy.Scope, owned: lazy.Scope.BubbleOwner != null));
        }
    }

    private void CompleteBuild(WindowToken t, PrebuildState st, PrebuildBranch br)
    {
        st.Building = null;
        if (br.Units.Count == 0) { MarkDone(t, br, "built"); return; }
        st.Built.Add(br);   // laid out by TickReveal once the window is on screen
    }

    // CondBinding.Apply is about to show this branch: finish it NOW (build the rest, re-activate every unit, un-stage).
    private void FinishBranch(WindowToken t, PrebuildBranch br)
    {
        if (br.Done) return;
        var hT = HitchProbe.Begin();
        var t0 = Stopwatch.GetTimestamp();
        StartBranch(t, br);   // a pre-claimed KeepAlive branch shown before the background reached it
        try { br.Items += br.Work.RunAll(item => BuildStep(t, br, item)); }
        catch (Exception ex) { AbortBranch(t, br, ex); return; }
        RevealAll(br);
        if (br.Staged) Unstage(br);
        br.FinishMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        MarkDone(t, br, "shown");
        HitchProbe.End(br.FinishLabel, hT);
    }

    // A throwing element build: show what was built (like a lazy build that threw midway) and move on.
    private void AbortBranch(WindowToken t, PrebuildBranch br, Exception ex)
    {
        br.Work.Clear();
        RevealAll(br);
        if (br.Staged) Unstage(br);
        Warn?.Invoke($"[Prebuild] {br.Name} threw: {ex.Message}");
        MarkDone(t, br, "aborted");
    }

    private static void RevealAll(PrebuildBranch br)
    {
        for (; br.Revealed < br.Reveal.Count; br.Revealed++)
            if (br.Reveal[br.Revealed] != null) br.Reveal[br.Revealed].SetActive(true);
    }

    private void MarkDone(WindowToken t, PrebuildBranch br, string how)
    {
        br.Done = true;
        var c = br.Cond;
        if (br.Then ? c.BuildThen == br.Finish : c.BuildElse == br.Finish)
        { if (br.Then) c.BuildThen = null; else c.BuildElse = null; }
        if (t.Prebuild is { } st)
        {
            if (st.Building == br) st.Building = null;
            if (st.Revealing == br) st.Revealing = null;
            st.Built.Remove(br);
        }
        if (HitchProbe.IsEnabled) Log?.Invoke(br.Summary(how));
    }
}
