using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;
using static Stellar.Application.Tests.NativeUi.NativeUiServiceTests;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>In-game fix round 2026-11-11: inner wrapper losing late children, re-migration of offsets computed against
/// an empty inner wrapper, saved visibility for absent / game-hidden elements, transient hide on a game-hidden element.</summary>
public sealed class NativeUiRegressionTests
{
    private const string InnerId = "gameui.inner";
    private const string IP = "slot0.gameui.inner.1920x1080";
    private static readonly NativeUiEntryDescriptor Inner = new(InnerId, "Inner", "zuiroot/inner") { Wrap = NativeUiWrapMode.Inner };
    private static readonly NativeUiEntryDescriptor Boss =
        new("gameui.boss", "Boss", "zuiroot/boss") { DefaultFraction = new WindowRect(0.3f, 0.1f, 0.4f, 0.1f) };

    private static NativeUiService.EntryState Entry(NativeUiService svc, string id)
    {
        foreach (var e in svc.Entries) if (e.Descriptor.Id == id) return e;
        throw new KeyNotFoundException(id);
    }

    [Fact] // The game adds a child beside our inner wrapper → wrap no longer intact → re-applied (adopted) with the offset.
    public void InnerElement_GainsChildAfterWrap_IsReappliedWithOffset()
    {
        var (svc, adapter, cfg, _) = New(Inner);
        cfg.Set($"{IP}.ox", 20f); cfg.Set($"{IP}.oy", 4f); cfg.Set($"{IP}.mv", 2f);
        svc.Tick(0.2f, Res);
        var applies = adapter.ApplyCount;
        adapter.WrapIntact = false;                          // childCount != 1: a late child landed beside the wrapper
        svc.Tick(0.1f, Res);
        Assert.True(adapter.ApplyCount > applies);
        Assert.Equal((20f, 4f, true), adapter.Applied[InnerId]);
    }

    [Fact] // An inner offset saved WITHOUT the migration marker (could be against an empty wrapper) re-migrates once.
    public void InnerOffset_WithoutMarker_ReMigratesFromLegacyRect()
    {
        var (svc, _, cfg, _) = New(Inner);
        cfg.Set($"{IP}.x", 300f); cfg.Set($"{IP}.y", 150f);
        cfg.Set($"{IP}.ox", -146.7f); cfg.Set($"{IP}.oy", 212f);   // the untrusted value
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);                                           // measure passes (one element each) → migrate
        svc.Tick(1f, Res);
        Assert.Equal(145f, cfg.Get($"{IP}.ox", 0f));
        Assert.Equal(-65f, cfg.Get($"{IP}.oy", 0f));
        Assert.Equal(2f, cfg.Get($"{IP}.mv", 0f));
    }

    [Fact] // With the marker the offset is trusted (and an OUTER entry without a marker is never re-migrated).
    public void MarkedInnerOffset_AndOuterOffset_AreKept()
    {
        var (svc, adapter, cfg, _) = New(Inner);
        cfg.Set($"{IP}.x", 300f); cfg.Set($"{IP}.y", 150f); cfg.Set($"{IP}.ox", 9f); cfg.Set($"{IP}.oy", 8f); cfg.Set($"{IP}.mv", 2f);
        cfg.Set($"{P}.x", 300f); cfg.Set($"{P}.y", 150f); cfg.Set($"{P}.ox", 7f); cfg.Set($"{P}.oy", 6f);
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        Assert.Equal((9f, 8f, true), adapter.Applied[InnerId]);
        Assert.Equal((7f, 6f, true), adapter.Applied[Id]);
    }

    [Fact] // A flung legacy rect far off-screen migrates to an ON-screen offset.
    public void LegacyRect_FarOffScreen_IsClampedBeforeMigrating()
    {
        var (svc, _, cfg, _) = New();
        cfg.Set($"{P}.x", -19700f); cfg.Set($"{P}.y", -648f);
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        Assert.Equal(-5f, cfg.Get($"{P}.ox", 0f));            // clamped to x=0: (0 − 10) / 2
        Assert.Equal(10f, cfg.Get($"{P}.oy", 0f));            // clamped to y=0: −(0 − 20) / 2
    }

    [Fact] // Absent element with saved visible=true → placeholder is NOT hidden-by-user; nothing saved → visible too.
    public void AbsentPlaceholder_UsesSavedVisibility_DefaultsVisible()
    {
        var (svc, adapter, cfg, _) = New(Boss);
        cfg.Set("slot0.gameui.boss.1920x1080.x", 500f); cfg.Set("slot0.gameui.boss.1920x1080.y", 50f);
        cfg.Set("slot0.gameui.boss.1920x1080.visible", true);
        adapter.Absent.Add("gameui.boss");
        svc.Tick(0.2f, Res);
        Assert.True(Entry(svc, "gameui.boss").Visible);

        var (svc2, adapter2, _, _) = New(Boss);
        adapter2.Absent.Add("gameui.boss");
        svc2.Tick(0.2f, Res);
        Assert.True(Entry(svc2, "gameui.boss").Visible);
        Assert.Contains(svc2.EditableElements(), el => el.Id == "gameui.boss" && el.Visible);
    }

    [Fact] // A saved hide (visible=false) IS shown as hidden-by-user even while the element is absent.
    public void AbsentPlaceholder_SavedHidden_IsHiddenByUser()
    {
        var (svc, adapter, cfg, _) = New(Boss);
        cfg.Set("slot0.gameui.boss.1920x1080.x", 500f); cfg.Set("slot0.gameui.boss.1920x1080.visible", false);
        adapter.Absent.Add("gameui.boss");
        svc.Tick(0.2f, Res);
        Assert.False(Entry(svc, "gameui.boss").Visible);
    }

    [Fact] // Transient hide on a resolved but GAME-hidden (inactive) element: undo restores it shown (scale 1 → unwrapped).
    public void TransientHide_OnGameHiddenElement_UndoRestoresVisible()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.ox", 5f); cfg.Set($"{P}.oy", 0f); cfg.Set($"{P}.visible", true);
        adapter.Showing = false;                              // solo player: node_team inactive
        svc.Tick(0.2f, Res);
        svc.SetTransientHidden(true);
        Assert.Equal((5f, 0f, false), adapter.Applied[Id]);
        svc.SetTransientHidden(false);
        Assert.Equal((5f, 0f, true), adapter.Applied[Id]);
    }

    [Fact] // A legacy element the game isn't showing keeps its saved visible=true while it waits to migrate.
    public void LegacyRect_GameHiddenElement_KeepsSavedVisible()
    {
        var (svc, adapter, cfg, _) = New();
        cfg.Set($"{P}.x", 300f); cfg.Set($"{P}.y", 150f); cfg.Set($"{P}.visible", true);
        adapter.Showing = false;
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        Assert.Equal((0f, 0f, true), adapter.Applied[Id]);
        Assert.True(cfg.Get($"{P}.visible", false));
        adapter.Showing = true;
        svc.Tick(1f, Res);                                    // now seen → migrates, still visible
        Assert.Equal((145f, -65f, true), adapter.Applied[Id]);
    }
}
