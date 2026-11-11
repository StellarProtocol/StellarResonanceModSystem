using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;
using static Stellar.Application.Tests.NativeUi.NativeUiServiceTests;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Last-seen rects are only remembered once SETTLED (2026-11-11 Quickbar bug): the game's skill-bar hide fades
/// the slot children to alpha 0 before the bar counts as hidden, so mid-fade it measured as just its [L Ctrl] cluster
/// and that transient rect became the placeholder.</summary>
public sealed class NativeUiSeenSettleTests
{
    private const string S = "seen.gameui.test.1920x1080";
    private static readonly WindowRect Full = new(10, 20, 100, 40);
    private static readonly WindowRect Shrunk = new(80, 10, 30, 50);   // the fading bar: only one cluster left

    private static NativeUiService.EntryState Entry(NativeUiService svc, string id)
    {
        foreach (var e in svc.Entries) if (e.Descriptor.Id == id) return e;
        throw new KeyNotFoundException(id);
    }

    [Fact] // A run settles only after the dwell; a sample within 2 px on every edge continues it.
    public void Settle_NeedsDwell_WithinEdgeTolerance()
    {
        var s = default(NativeUiService.SeenStability);
        Assert.False(NativeUiService.AdvanceSettle(ref s, Full, 0f));
        Assert.False(NativeUiService.AdvanceSettle(ref s, new WindowRect(11, 21, 100, 39), 0.5f));
        Assert.True(NativeUiService.AdvanceSettle(ref s, new WindowRect(9, 19, 101, 41), 1f));
    }

    [Fact] // A sample off by more than 2 px on any edge (here: only the right edge) restarts the run.
    public void Settle_EdgeOutsideTolerance_RestartsRun()
    {
        var s = default(NativeUiService.SeenStability);
        NativeUiService.AdvanceSettle(ref s, Full, 0f);
        Assert.False(NativeUiService.AdvanceSettle(ref s, new WindowRect(10, 20, 103, 40), 1f));
        Assert.False(NativeUiService.AdvanceSettle(ref s, new WindowRect(10, 20, 103, 40), 1.5f));
        Assert.True(NativeUiService.AdvanceSettle(ref s, new WindowRect(10, 20, 103, 40), 2f));
    }

    [Fact] // Compared to the run's FIRST rect: a slow shrink in sub-tolerance steps can't creep through as "stable".
    public void Settle_SlowDrift_NeverSettles()
    {
        var s = default(NativeUiService.SeenStability);
        var settled = false;
        for (var i = 0; i < 20; i++)
            settled |= NativeUiService.AdvanceSettle(ref s, new WindowRect(10 + i * 1.5f, 20, 100 - i * 1.5f, 40), i * 0.5f);
        Assert.False(settled);
    }

    [Fact] // The bug: a shrunken mid-fade measurement, then the bar is hidden → nothing remembered, default rect used.
    public void TransientShrink_ThenHidden_IsNotRemembered()
    {
        var (svc, adapter, cfg, _) = New();
        adapter.Base = Shrunk;
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);                 // measured once mid-fade
        adapter.Showing = false;           // fade finished: hidden by the game
        svc.Tick(1f, Res);
        svc.Tick(1f, Res);
        Assert.DoesNotContain($"{S}.x", cfg.Keys);
        Assert.True(svc.TryGetEditView(Entry(svc, Id), out var ph));
        Assert.Equal(NativeUiPresence.HiddenByGame, ph.Presence);
        Assert.NotEqual(Shrunk.Width, ph.Rect.Width);
    }

    [Fact] // Shown → hidden → shown again with the same rect: the run must be consecutive showing samples.
    public void HiddenSample_BreaksTheRun()
    {
        var (svc, adapter, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);                 // run starts
        adapter.Showing = false;
        svc.Tick(1f, Res);                 // hidden sample resets it
        adapter.Showing = true;
        svc.Tick(1f, Res);                 // run restarts here
        Assert.DoesNotContain($"{S}.x", cfg.Keys);
        svc.Tick(1f, Res);
        Assert.Equal(100f, cfg.Get($"{S}.w", 0f));
    }

    [Fact] // Self-heal: a transient rect saved by an older build is replaced once the full bar is shown and settled.
    public void SavedTransientSeen_IsOverwritten_ByNextSettledRect()
    {
        var (svc, _, cfg, _) = New();
        cfg.Set($"{S}.x", 1590.9f); cfg.Set($"{S}.y", 1270.8f); cfg.Set($"{S}.w", 159.5f); cfg.Set($"{S}.h", 178.3f);
        cfg.Set($"{S}.s", 2f);
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        svc.Tick(1f, Res);
        Assert.Equal(10f, cfg.Get($"{S}.x", 0f));
        Assert.Equal(100f, cfg.Get($"{S}.w", 0f));
        Assert.Equal(40f, cfg.Get($"{S}.h", 0f));
    }

    [Fact] // The edit-mode live outline still follows the live (even transient) rect while the element is showing.
    public void LiveOutline_FollowsLiveRect_BeforeSettling()
    {
        var (svc, adapter, _, _) = New();
        adapter.Base = Shrunk;
        svc.Tick(0.2f, Res);
        Assert.True(svc.TryGetEditView(Entry(svc, Id), out var v));
        Assert.Equal(NativeUiPresence.Showing, v.Presence);
        Assert.Equal(Shrunk, v.Rect);
    }
}
