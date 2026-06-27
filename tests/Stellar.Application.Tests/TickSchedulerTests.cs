using System.Collections.Generic;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class TickSchedulerTests
{
    private static TickScheduler New(out List<int> rateEvents)
    {
        var s = new TickScheduler(maxHoldSeconds: 10.0);
        var ev = new List<int>();
        s.MasterRateChanged += hz => ev.Add(hz);
        rateEvents = ev;
        return s;
    }

    [Fact]
    public void Master_defaults_to_global()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void Static_rate_above_global_raises_master()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", staticRateHz: 60, allowSelfControl: false);
        Assert.Equal(60, s.MasterRateHz);
        Assert.Equal(60, s.EffectiveRateFor("a"));
    }

    [Fact]
    public void Static_rate_below_global_does_not_lower_master()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", staticRateHz: 10, allowSelfControl: false);
        Assert.Equal(30, s.MasterRateHz);
        Assert.Equal(10, s.EffectiveRateFor("a"));
    }

    [Fact]
    public void Follow_global_plugin_tracks_global()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", staticRateHz: null, allowSelfControl: false);
        Assert.Equal(30, s.EffectiveRateFor("a"));
        s.SetGlobalRate(60);
        Assert.Equal(60, s.EffectiveRateFor("a"));
    }

    [Fact]
    public void MasterRateChanged_fires_only_on_change()
    {
        var s = New(out var events);
        s.SetGlobalRate(30);
        s.SetGlobalRate(30);
        s.SetGlobalRate(60);
        Assert.Equal(new[] { 60 }, events.ToArray());
    }

    [Fact]
    public void Rates_are_clamped_to_supported_range()
    {
        var s = New(out _);
        s.SetGlobalRate(99999);
        Assert.Equal(240, s.MasterRateHz);
    }

    [Fact]
    public void Unregister_recomputes_master()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", 120, false);
        Assert.Equal(120, s.MasterRateHz);
        s.UnregisterPlugin("a");
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void Beat_fires_follow_global_plugin_every_beat_at_matching_rate()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        var fired = 0;
        s.RegisterPlugin("a", _ => fired++);
        s.ConfigurePlugin("a", null, false);
        for (var i = 0; i < 5; i++) s.Beat(1f / 30f);
        Assert.Equal(5, fired);
    }

    [Fact]
    public void Beat_downsamples_a_slow_plugin_under_a_fast_master()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        var slowFired = 0;
        s.RegisterPlugin("slow", _ => slowFired++);
        s.ConfigurePlugin("slow", 30, false);
        s.RegisterPlugin("fast", _ => { });
        s.ConfigurePlugin("fast", null, true);
        using var ramp = s.RequestDynamicRate("fast", 150);
        Assert.Equal(150, s.MasterRateHz);
        for (var i = 0; i < 30; i++) s.Beat(1f / 150f);
        Assert.InRange(slowFired, 5, 7);
    }

    [Fact]
    public void RequestDynamicRate_is_inert_without_permission()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", null, allowSelfControl: false);
        var scope = s.RequestDynamicRate("a", 240);
        Assert.False(scope.IsActive);
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void RequestDynamicRate_raises_master_when_permitted_and_reverts_on_dispose()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", null, allowSelfControl: true);
        var scope = s.RequestDynamicRate("a", 240);
        Assert.True(scope.IsActive);
        Assert.Equal(240, s.MasterRateHz);
        scope.Dispose();
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void Stacked_ramps_take_the_maximum()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", null, true);
        var lo = s.RequestDynamicRate("a", 60);
        var hi = s.RequestDynamicRate("a", 144);
        Assert.Equal(144, s.MasterRateHz);
        hi.Dispose();
        Assert.Equal(60, s.MasterRateHz);
        lo.Dispose();
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        var s = New(out _);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", null, true);
        var scope = s.RequestDynamicRate("a", 120);
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void A_leaked_ramp_auto_expires_after_max_hold()
    {
        var s = new TickScheduler(maxHoldSeconds: 1.0);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", null, true);
        _ = s.RequestDynamicRate("a", 240);
        Assert.Equal(240, s.MasterRateHz);
        for (var i = 0; i < 250; i++) s.Beat(1f / 240f);
        Assert.Equal(30, s.MasterRateHz);
    }

    [Fact]
    public void Unregister_during_update_defers_removal_and_skips_no_plugin_that_beat()
    {
        var s = new TickScheduler();
        s.SetGlobalRate(30);
        int aFired = 0, bFired = 0, cFired = 0;
        s.RegisterPlugin("a", _ => { aFired++; s.UnregisterPlugin("b"); });
        s.ConfigurePlugin("a", null, false);
        s.RegisterPlugin("b", _ => bFired++);
        s.ConfigurePlugin("b", null, false);
        s.RegisterPlugin("c", _ => cFired++);
        s.ConfigurePlugin("c", null, false);

        s.Beat(1f / 30f);                 // a unregisters b mid-beat -> deferred; b still ticks, c not skipped
        Assert.Equal(1, aFired);
        Assert.Equal(1, bFired);
        Assert.Equal(1, cFired);

        s.Beat(1f / 30f);                 // b is now removed
        Assert.Equal(2, aFired);
        Assert.Equal(1, bFired);          // unchanged — b gone
        Assert.Equal(2, cFired);
    }

    [Fact]
    public void Leaked_ramp_logs_on_auto_release()
    {
        var logs = new System.Collections.Generic.List<string>();
        var s = new TickScheduler(maxHoldSeconds: 1.0, log: logs.Add);
        s.SetGlobalRate(30);
        s.RegisterPlugin("a", _ => { });
        s.ConfigurePlugin("a", null, true);
        _ = s.RequestDynamicRate("a", 240);
        for (var i = 0; i < 250; i++) s.Beat(1f / 240f);
        Assert.Equal(30, s.MasterRateHz);
        Assert.Contains(logs, m => m.Contains("auto-released"));
    }
}
