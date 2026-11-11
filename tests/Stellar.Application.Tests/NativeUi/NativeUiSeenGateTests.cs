using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;
using static Stellar.Application.Tests.NativeUi.NativeUiServiceTests;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Last-seen gate + versioned seen keys (2026-11-11 Quickbar bug): in town the bar only shows while L Ctrl is
/// held, which expands the skill wheel — a STEADY state, so it passed the settle dwell and the placeholder became the
/// expanded 1149x372 px box. A closed gate (adapter <c>SeenAllowed=false</c>) must break the run; SeenKeyVersion 2
/// must ignore the values saved before the fix.</summary>
public sealed class NativeUiSeenGateTests
{
    private const string S = "seen.gameui.test.1920x1080";
    private const string V2Id = "gameui.v2";
    private const string S2 = "seen.gameui.v2.v2.1920x1080";

    private static NativeUiService.EntryState Entry(NativeUiService svc, string id)
    {
        foreach (var e in svc.Entries) if (e.Descriptor.Id == id) return e;
        throw new KeyNotFoundException(id);
    }

    [Fact] // Gate closed (wheel expanded) for many passes: steady, but never remembered; the outline still follows it.
    public void GateClosed_SteadyRect_IsNotSaved()
    {
        var (svc, adapter, cfg, _) = New();
        adapter.SeenAllowed = false;
        svc.Tick(0.2f, Res);
        for (var i = 0; i < 5; i++) svc.Tick(1f, Res);
        Assert.DoesNotContain($"{S}.x", cfg.Keys);
        Assert.True(svc.TryGetEditView(Entry(svc, Id), out var v));
        Assert.Equal(NativeUiPresence.Showing, v.Presence);
        Assert.Equal(FakeNativeUiAdapter.BaseRect, v.Rect);
    }

    [Fact] // Gate open (rest scale): the settled rect is saved as before.
    public void GateOpen_SettledRect_IsSaved()
    {
        var (svc, _, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);
        svc.Tick(1f, Res);
        Assert.Equal(100f, cfg.Get($"{S}.w", 0f));
        Assert.Equal(40f, cfg.Get($"{S}.h", 0f));
    }

    [Fact] // A closed-gate sample in the middle of a run restarts it (same rect on both sides — still consecutive-only).
    public void GateClosedSample_BreaksTheRun()
    {
        var (svc, adapter, cfg, _) = New();
        svc.Tick(0.2f, Res);
        svc.Tick(1f, Res);                 // run starts
        adapter.SeenAllowed = false;
        svc.Tick(1f, Res);                 // expanded sample resets it
        adapter.SeenAllowed = true;
        svc.Tick(1f, Res);                 // run restarts
        Assert.DoesNotContain($"{S}.x", cfg.Keys);
        svc.Tick(1f, Res);
        Assert.Equal(100f, cfg.Get($"{S}.w", 0f));
    }

    [Fact] // SeenKeyVersion 2: an old (v1-key) expanded value is ignored → the placeholder uses DefaultFraction.
    public void VersionedSeenKey_IgnoresOldValue()
    {
        var d = new NativeUiEntryDescriptor(V2Id, "V2", "zuiroot/v2")
            { SeenKeyVersion = 2, DefaultFraction = new(0.25f, 0.5f, 0.25f, 0.1f) };
        var (svc, adapter, cfg, _) = New(d);
        const string old = "seen.gameui.v2.1920x1080";
        cfg.Set($"{old}.x", 700f); cfg.Set($"{old}.y", 400f); cfg.Set($"{old}.w", 1149f); cfg.Set($"{old}.h", 372f);
        cfg.Set($"{old}.s", 2f);
        adapter.Showing = false;           // game-hidden: placeholder
        svc.Tick(0.2f, Res);
        Assert.True(svc.TryGetEditView(Entry(svc, V2Id), out var v));
        Assert.Equal(NativeUiPresence.HiddenByGame, v.Presence);
        Assert.Equal(new WindowRect(480f, 540f, 480f, 108f), v.Rect);
    }

    [Fact] // SeenKeyVersion 2 writes the ".v2." keys and never touches the old ones; v1 entries keep their key shape.
    public void VersionedSeenKey_WritesV2Keys()
    {
        var d = new NativeUiEntryDescriptor(V2Id, "V2", "zuiroot/v2") { SeenKeyVersion = 2 };
        var (svc, _, cfg, _) = New(d);
        svc.Tick(0.2f, Res);
        for (var i = 0; i < 6; i++) svc.Tick(1f, Res);   // the 1 Hz pass measures one element per pass
        Assert.Equal(100f, cfg.Get($"{S2}.w", 0f));
        Assert.DoesNotContain("seen.gameui.v2.1920x1080.w", cfg.Keys);
        Assert.Equal(100f, cfg.Get($"{S}.w", 0f));      // the unversioned entry: unchanged key
    }
}
