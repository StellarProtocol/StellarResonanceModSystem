using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class TimeOfDayServiceTests
{
    // Server-driven open world at 15:00.
    private sealed class FakeBackend : ITimeOfDayBackend
    {
        public float Hour = 15f;
        public bool ServerDriven = true;
        public bool Ready = true;
        public readonly List<string> Calls = new();
        public int EnsureHooksCalls;
        public void EnsureHooks() => EnsureHooksCalls++;
        public bool IsAvailable => true;
        public bool IsReady => Ready;
        public float? ReadHour() => Hour;
        public bool? ReadServerDriven() => ServerDriven;
        public void Pin(float hour) { Calls.Add($"pin {hour}"); ServerDriven = false; Hour = hour; }
        public void ReleaseToServer() { Calls.Add("server"); ServerDriven = true; }
    }

    [Fact]
    public void Pin_stops_server_time_and_sets_the_hour()
    {
        var b = new FakeBackend();
        var pin = new TimeOfDayService(b).Add(20f);
        Assert.Equal(new[] { "pin 20" }, b.Calls);
        Assert.True(pin.IsActive);
    }

    [Fact]
    public void Newest_pin_wins_and_dispose_falls_back()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        var older = s.Add(8f);
        var newer = s.Add(22f);
        Assert.Equal(22f, b.Hour);
        newer.Dispose();
        Assert.Equal(8f, b.Hour);
        Assert.False(b.ServerDriven);
        older.Dispose();
        Assert.True(b.ServerDriven);
        Assert.Equal(new[] { "pin 8", "pin 22", "pin 8", "server" }, b.Calls);
    }

    [Fact]
    public void Disposing_a_shadowed_pin_does_not_move_the_clock()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        var older = s.Add(8f);
        s.Add(22f);
        b.Calls.Clear();
        older.Dispose();
        Assert.Empty(b.Calls);
        Assert.Equal(22f, b.Hour);
    }

    [Fact]
    public void Last_dispose_hands_time_back_to_the_server_once()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        var p = s.Add(6f);
        p.Dispose();
        p.Dispose();
        s.Reassert();
        Assert.Equal(new[] { "pin 6", "server" }, b.Calls);
        Assert.False(p.IsActive);
    }

    [Fact]
    public void SetHour_on_the_newest_pin_applies_at_once()
    {
        var b = new FakeBackend();
        var p = new TimeOfDayService(b).Add(6f);
        p.SetHour(13.5f);
        Assert.Equal(13.5f, b.Hour);
    }

    [Fact]
    public void SetHour_on_a_shadowed_pin_is_remembered_for_fallback()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        var older = s.Add(6f);
        var newer = s.Add(22f);
        older.SetHour(9f);
        Assert.Equal(22f, b.Hour);
        newer.Dispose();
        Assert.Equal(9f, b.Hour);
    }

    [Fact]
    public void SetHour_after_dispose_is_a_noop()
    {
        var b = new FakeBackend();
        var p = new TimeOfDayService(b).Add(6f);
        p.Dispose();
        b.Calls.Clear();
        p.SetHour(12f);
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Reassert_writes_only_when_the_game_moved_the_clock()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        s.Add(20f);
        b.Calls.Clear();
        s.Reassert();
        Assert.Empty(b.Calls);                // nothing changed: no write
        b.Hour = 10f; b.ServerDriven = false; // an interior pinned 10:00
        s.Reassert();
        Assert.Equal(new[] { "pin 20" }, b.Calls);
        b.Calls.Clear();
        b.ServerDriven = true;                // leaving it handed time back to the server
        s.Reassert();
        Assert.Equal(new[] { "pin 20" }, b.Calls);
    }

    [Fact]
    public void Reassert_tolerates_the_24_to_0_wrap()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        s.Add(24f);
        b.Calls.Clear();
        b.Hour = 0f;   // the game reports 24:00 as 00:00
        s.Reassert();
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Reassert_with_no_pin_never_touches_the_clock()
    {
        var b = new FakeBackend { ServerDriven = false, Hour = 10f };   // an interior's own pin
        new TimeOfDayService(b).Reassert();
        Assert.Empty(b.Calls);
    }

    [Fact]
    public void Pin_while_not_ready_applies_on_the_next_reassert()
    {
        var b = new FakeBackend { Ready = false };
        var s = new TimeOfDayService(b);
        s.Add(5f);
        Assert.Empty(b.Calls);
        b.Ready = true;
        s.Reassert();
        Assert.Equal(new[] { "pin 5" }, b.Calls);
    }

    [Fact]
    public void Hand_back_while_not_ready_stays_pending()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        var p = s.Add(5f);
        b.Ready = false;
        p.Dispose();
        Assert.Equal(new[] { "pin 5" }, b.Calls);
        b.Ready = true;
        s.Reassert();
        Assert.Equal(new[] { "pin 5", "server" }, b.Calls);
    }

    [Fact]
    public void A_pin_never_applied_never_hands_back()
    {
        var b = new FakeBackend { Ready = false };
        var s = new TimeOfDayService(b);
        s.Add(5f).Dispose();
        b.Ready = true;
        s.Reassert();
        Assert.Empty(b.Calls);
    }

    [Theory]
    [InlineData(-3f, 0f)]
    [InlineData(30f, 24f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(12.25f, 12.25f)]
    public void Hours_are_clamped(float input, float expected) => Assert.Equal(expected, TimeOfDayService.Clamp(input));

    [Fact]
    public void Unload_release_drops_that_plugins_pins_and_falls_back()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        var pluginA = new PluginTimeOfDay(s);
        var pluginB = new PluginTimeOfDay(s);
        pluginA.Pin(7f);
        pluginB.Pin(19f);
        pluginB.Pin(21f);
        pluginB.ReleaseAll();
        Assert.Equal(7f, b.Hour);
        Assert.Equal(0, pluginB.TrackedCount);
        pluginA.ReleaseAll();
        Assert.True(b.ServerDriven);
        Assert.Equal(new[] { "pin 7", "pin 19", "pin 21", "pin 7", "server" }, b.Calls);
    }

    [Fact]
    public void Facade_prunes_disposed_pins()
    {
        var p = new PluginTimeOfDay(new TimeOfDayService(new FakeBackend()));
        p.Pin(3f).Dispose();
        p.Pin(4f);
        Assert.Equal(1, p.TrackedCount);
    }

    // Review fix 2: game hooks install lazily — the first Pin asks for them; reads / re-asserts never do.
    [Fact]
    public void Pin_asks_the_backend_for_its_hooks_and_reassert_does_not()
    {
        var b = new FakeBackend();
        var s = new TimeOfDayService(b);
        s.Reassert();
        _ = s.CurrentHour;
        Assert.Equal(0, b.EnsureHooksCalls);
        s.Add(9f);
        Assert.Equal(1, b.EnsureHooksCalls);
    }
}
