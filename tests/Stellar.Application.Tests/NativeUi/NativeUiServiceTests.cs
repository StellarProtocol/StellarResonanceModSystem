using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Wrapper-ownership semantics of <see cref="NativeUiService"/>: only customized elements are wrapped, the
/// offset lives in wrapper-local units, hide = wrapper scale 0, no re-assert loop, re-wrap on a lost wrapper / rebuilt
/// element, legacy-rect migration, per slot × resolution persistence. Edit-mode placeholders: NativeUiPlaceholderTests.</summary>
public sealed class NativeUiServiceTests
{
    internal static readonly Resolution Res = new(1920, 1080);
    internal const string Id = "gameui.test";
    internal const string P = "slot0.gameui.test.1920x1080";

    internal sealed class SlotBox { public int Value; }

    internal static (NativeUiService svc, FakeNativeUiAdapter adapter, NativeUiMemoryConfig cfg, SlotBox slot) New(
        params NativeUiEntryDescriptor[] extra)
    {
        var adapter = new FakeNativeUiAdapter();
        var cfg = new NativeUiMemoryConfig();
        var slot = new SlotBox();
        var targets = new List<NativeUiEntryDescriptor> { new(Id, "Test", "zuiroot/x/y") };
        targets.AddRange(extra);
        return (new NativeUiService(adapter, cfg, () => slot.Value, new NativeUiNullLog(), targets), adapter, cfg, slot);
    }

    [Fact] // The regression: resolving an element the user never touched must not wrap or write it.
    public void UnmodifiedEntry_IsNeverWrapped()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        Assert.Equal(1, adapter.ResolveCount);
        Assert.Equal(0, adapter.ApplyCount);
    }

    [Fact] // A drag's screen delta becomes a wrapper offset in local units (÷ px-per-unit, y flipped to y-up).
    public void SetRect_MovesWrapperOffset_InLocalUnits()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);                                     // resolve; measure base rect (10,20) at 2 px/unit
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));     // +100 px right, +100 px down
        Assert.Equal((50f, -50f, true), adapter.Applied[Id]);
    }

    [Fact] // There is no re-assert loop any more: once applied, later ticks never re-write an intact wrapper.
    public void AfterUserMove_TicksDoNotReapply()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        var applies = adapter.ApplyCount;
        for (var i = 0; i < 30; i++) svc.Tick(0.2f, Res);
        Assert.Equal(applies, adapter.ApplyCount);
    }

    [Fact] // Hide = scale 0 on OUR wrapper (Apply visible:false) at the current offset; persisted at once.
    public void SetVisibleFalse_AppliesHideOnWrapper_AndPersists()
    {
        var (svc, adapter, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetVisible(Id, false);
        Assert.Equal((0f, 0f, false), adapter.Applied[Id]);
        Assert.False(cfg.Get($"{P}.visible", true));
    }

    [Fact] // The game dropped our wrapper (e.g. re-parented the node): the 10 Hz liveness check re-wraps it.
    public void LostWrapper_IsRewrapped_OnNextLivenessPass()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        var applies = adapter.ApplyCount;
        adapter.WrapIntact = false;
        svc.Tick(0.1f, Res);
        Assert.Equal(applies + 1, adapter.ApplyCount);
        Assert.Equal((50f, -50f, true), adapter.Applied[Id]);   // same in-memory offset, no measurement needed
    }

    [Fact] // Scene change destroyed the element: re-resolve the rebuilt one and re-apply the offset immediately.
    public void StaleHandle_ReResolves_AndReappliesOffset()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.ox", 30f); cfg.Set($"{P}.oy", -12f); cfg.Set($"{P}.visible", true);
        svc.Tick(0.2f, Res);
        Assert.Equal(1, adapter.ResolveCount);
        adapter.Alive = false;                                   // the game rebuilt the battle HUD
        adapter.Applied.Clear();
        svc.Tick(0.1f, Res);
        Assert.Equal(2, adapter.ResolveCount);
        Assert.Equal((30f, -12f, true), adapter.Applied[Id]);
    }

    [Fact]
    public void SavedOffset_IsAppliedOnResolve()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.ox", 30f); cfg.Set($"{P}.oy", -12f); cfg.Set($"{P}.visible", false);
        svc.Tick(0.2f, Res);
        Assert.Equal((30f, -12f, false), adapter.Applied[Id]);
    }

    [Fact] // Drag-move must not disk-write every frame; persistence waits for Commit (release).
    public void SetRect_DoesNotPersist_UntilCommit()
    {
        var (svc, _, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        Assert.DoesNotContain($"{P}.ox", cfg.Keys);
        svc.Commit(Id);
        Assert.Equal(50f, cfg.Get($"{P}.ox", 0f));
        Assert.Equal(-50f, cfg.Get($"{P}.oy", 0f));
    }

    [Fact] // Pre-wrapper layouts (.x/.y screen top-left) migrate once the element is seen: offset = saved − default.
    public void LegacyRect_MigratesToOffset_WhenSeen()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.x", 300f); cfg.Set($"{P}.y", 150f); cfg.Set($"{P}.visible", true);
        svc.Tick(0.2f, Res);                                     // resolve (offset 0 until measured)
        svc.Tick(1f, Res);                                       // 1 Hz measure pass → migrate
        Assert.Equal(145f, cfg.Get($"{P}.ox", 0f));              // (300 − 10) / 2
        Assert.Equal(-65f, cfg.Get($"{P}.oy", 0f));              // −(150 − 20) / 2
        Assert.Equal((145f, -65f, true), adapter.Applied[Id]);
        Assert.Contains($"{P}.x", cfg.Keys);                     // old keys are not deleted by this change
    }

    [Fact] // A legacy element the user had hidden stays hidden from the first frame (before any measurement).
    public void LegacyHidden_IsHiddenImmediately()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.x", 300f); cfg.Set($"{P}.y", 150f); cfg.Set($"{P}.visible", false);
        svc.Tick(0.2f, Res);
        Assert.Equal((0f, 0f, false), adapter.Applied[Id]);
    }

    [Fact] // A legacy element the game isn't showing yet is not migrated against a bogus rect — it waits.
    public void LegacyRect_WaitsWhileGameHidesElement()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.x", 300f); cfg.Set($"{P}.y", 150f);
        adapter.Showing = false;
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        Assert.DoesNotContain($"{P}.ox", cfg.Keys);
    }

    [Fact]
    public void MigrationMath_SubtractsDefault_FlipsY_DividesByScale()
    {
        Assert.Equal((145f, -65f), NativeUiService.MigrateLegacyOffset(300, 150, new WindowRect(10, 20, 100, 40), 2f));
        Assert.Equal((0f, 0f), NativeUiService.ScreenDeltaToOffset(10, 10, 0f));   // unknown scale → no move
        Assert.Equal((20f, -8f), NativeUiService.OffsetToScreenDelta(10, 4, 2f));
    }

    [Fact] // Reset drops the override for EVERY resolution and unwraps the element.
    public void Reset_RemovesOverride_ForAllResolutions_AndUnwraps()
    {
        var (svc, adapter, cfg, _) = New();
        var other = new Resolution(2560, 1440);
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        svc.Commit(Id);
        svc.ReapplyForActiveSlot(other, applyVisibility: false);
        svc.Tick(0.2f, other);
        svc.SetRect(Id, new WindowRect(300, 400, 100, 40));
        svc.Commit(Id);
        Assert.Contains($"{P}.ox", cfg.Keys);
        Assert.Contains("slot0.gameui.test.2560x1440.ox", cfg.Keys);
        var unwraps = adapter.UnwrapCount;

        svc.ResetToOriginal(Id);

        Assert.DoesNotContain($"{P}.ox", cfg.Keys);
        Assert.DoesNotContain("slot0.gameui.test.2560x1440.ox", cfg.Keys);
        Assert.True(adapter.UnwrapCount > unwraps);
    }

    [Fact] // A layout slot captures native-UI offsets independently; a slot without an override unwraps.
    public void Slots_StoreOffsetsIndependently()
    {
        var (svc, adapter, cfg, slot) = New();
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        svc.Commit(Id);
        slot.Value = 1;
        var unwraps = adapter.UnwrapCount;
        svc.ReapplyForActiveSlot(Res);
        Assert.True(adapter.UnwrapCount > unwraps, "switching to a slot with no override unwraps");
        svc.SetRect(Id, new WindowRect(10, 220, 100, 40));      // +200 px down from the default (10,20)
        svc.Commit(Id);
        Assert.Equal(50f, cfg.Get($"{P}.ox", -1f));               // slot 0 untouched
        Assert.Equal(-100f, cfg.Get("slot1.gameui.test.1920x1080.oy", -1f));
    }

    [Fact] // Closest-resolution fallback within 10% (parity with the mod-window path).
    public void SavedOffset_UnderNearMissResolution_IsRecovered()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.ox", 30f); cfg.Set($"{P}.oy", 5f);
        svc.Tick(0.2f, new Resolution(1900, 1060));
        Assert.Equal((30f, 5f, true), adapter.Applied[Id]);
    }

    [Fact] // …but a FAR resolution never borrows an unrelated saved pose.
    public void SavedOffset_UnderFarResolution_IsNotBorrowed()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.ox", 30f); cfg.Set($"{P}.oy", 5f);
        svc.Tick(0.2f, new Resolution(800, 600));
        Assert.Equal(0, adapter.ApplyCount);
    }

    [Fact] // Resolution change to an unconfigured resolution = game default (offset 0); a user hide is preserved.
    public void ReapplyForActiveSlot_UnconfiguredResolution_ReturnsToDefault_KeepsVisibility()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.ox", 30f); cfg.Set($"{P}.oy", 5f); cfg.Set($"{P}.visible", false);
        svc.Tick(0.2f, Res);
        svc.ReapplyForActiveSlot(new Resolution(800, 600), applyVisibility: false);
        Assert.Equal((0f, 0f, false), adapter.Applied[Id]);     // offset 0, still hidden (wrapped at scale 0)

        cfg.Set($"{P}.visible", true);
        svc.ReapplyForActiveSlot(Res, applyVisibility: false);  // back: offset restored, visibility untouched
        Assert.Equal((30f, 5f, false), adapter.Applied[Id]);
    }

    [Fact] // A canvas-generation bump invalidates handles: the next Tick re-resolves on the SAME beat (armed).
    public void InvalidateResolvedHandles_ForcesReResolve_OnNextTick()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.InvalidateResolvedHandles();
        svc.Tick(0.016f, Res);
        Assert.Equal(2, adapter.ResolveCount);
    }

    [Fact] // Scene-enter signals arm an immediate absent-resolve pass (no 0.5 s wait).
    public void RequestRecheck_ResolvesAbsentElement_OnNextTick()
    {
        var (svc, adapter, _, _) = New();
        adapter.Absent.Add(Id);
        svc.Tick(0.2f, Res);
        adapter.Absent.Clear();
        svc.Tick(0.016f, Res);
        Assert.Equal(0, adapter.ResolveCount);                   // below every cadence → not yet
        svc.RequestRecheck();
        svc.Tick(0.016f, Res);
        Assert.Equal(1, adapter.ResolveCount);
    }

    [Fact] // Framework dispose unwraps every resolved element (mod isolation).
    public void OnFrameworkDispose_UnwrapsResolved()
    {
        var (svc, adapter, _, _) = New();
        svc.Tick(0.2f, Res);
        svc.SetRect(Id, new WindowRect(110, 120, 100, 40));
        var unwraps = adapter.UnwrapCount;
        svc.OnFrameworkDispose();
        Assert.Equal(unwraps + 1, adapter.UnwrapCount);
    }
}
