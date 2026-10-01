using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 4 / § 6 ISceneFreeze: ref-counted; hooks install on first use; STELLAR_FREEZE_NO_POSITIONS ⇒ no position
// hold; framework auto-release; an over-budget hold turns itself off and says so.
public sealed class SceneFreezeServiceTests
{
    private sealed class FakeBackend : ISceneFreezeBackend
    {
        public readonly List<string> Calls = new();
        public bool Holding;
        public event Action? HoldDisabled;
        public void EnsureHooks() => Calls.Add("hooks");
        public void FreezeAll(bool holdPositions) { Calls.Add($"freeze hold={holdPositions}"); Holding = holdPositions; }
        public void UnfreezeAll() { Calls.Add("unfreeze"); Holding = false; }
        public bool HoldsPositions => Holding;
        public void OverBudget() { Holding = false; HoldDisabled?.Invoke(); }
    }

    [Fact]
    public void First_token_freezes_with_hold_last_release_unfreezes()
    {
        var b = new FakeBackend();
        var s = new SceneFreezeService(b, positionsDisabled: false);
        var changes = new List<bool>();
        s.Changed += changes.Add;
        var t1 = s.Freeze();
        var t2 = s.Freeze();
        Assert.True(s.IsFrozen);
        Assert.True(s.HoldsPositions);
        t1.Dispose();
        Assert.True(s.IsFrozen);
        t2.Dispose();
        t2.Dispose();
        Assert.False(s.IsFrozen);
        Assert.Equal(new[] { "hooks", "freeze hold=True", "unfreeze" }, b.Calls);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public void Kill_switch_freezes_without_position_hold()
    {
        var b = new FakeBackend();
        var s = new SceneFreezeService(b, positionsDisabled: true);
        s.Freeze();
        Assert.Contains("freeze hold=False", b.Calls);
        Assert.False(s.HoldsPositions);
    }

    [Fact]
    public void ReleaseAll_unfreezes_and_kills_every_token()
    {
        var b = new FakeBackend();
        var s = new SceneFreezeService(b, false);
        var t = s.Freeze();
        s.ReleaseAll();
        Assert.False(s.IsFrozen);
        t.Dispose();
        Assert.Single(b.Calls, "unfreeze");
    }

    [Fact]
    public void Over_budget_hold_raises_Changed_and_reports_no_hold()
    {
        var b = new FakeBackend();
        var s = new SceneFreezeService(b, false);
        var changes = new List<bool>();
        s.Freeze();
        s.Changed += changes.Add;
        b.OverBudget();
        Assert.True(s.IsFrozen);
        Assert.False(s.HoldsPositions);
        Assert.Equal(new[] { true }, changes);
    }

    [Fact]
    public void Facade_release_drops_only_its_tokens_and_handlers()
    {
        var b = new FakeBackend();
        var s = new SceneFreezeService(b, false);
        var mine = new PluginSceneFreeze(s, new object());
        var calls = 0;
        mine.Changed += _ => calls++;
        var theirs = s.Freeze(new object());
        mine.Freeze();
        mine.ReleaseAll();
        Assert.True(s.IsFrozen);
        theirs.Dispose();
        Assert.False(s.IsFrozen);
        Assert.Equal(1, calls);   // only the first freeze (before ReleaseAll) reached the dropped handler
    }
}
