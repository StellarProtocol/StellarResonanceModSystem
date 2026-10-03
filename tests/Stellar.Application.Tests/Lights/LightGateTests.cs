using System.Linq;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Lights;

// Lights spec § 4 + § 6 (devkit-freecam 2026-10-03-photo-studio-lights-design.md), recon Run 13b: the character-lamp gate
// snapshots ALL override flags + the value + active, overrides ONLY creaturePointlightColorIntensity, and restores every one
// of them exactly — the game's own "off" restores nothing (recon row D).
public sealed class LightGateTests
{
    [Fact]
    public void Raise_snapshots_everything_before_its_first_write()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        LightGate.Raise(v, 2f);
        var firstWrite = log.Entries.FindIndex(e => e.StartsWith("set", System.StringComparison.Ordinal));
        var lastRead = log.Entries.FindLastIndex(e => e.StartsWith("get", System.StringComparison.Ordinal));
        Assert.True(lastRead < firstWrite, "every read (flags, value, active) precedes the first write");
        Assert.Equal(101, log.Entries.Count(e => e.StartsWith("get flag", System.StringComparison.Ordinal)));
        Assert.Contains("get value", log.Entries);
        Assert.Contains("get active", log.Entries);
    }

    [Fact]
    public void Raise_overrides_only_the_gate_parameter_and_activates()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var gate = LightGate.Raise(v, 2f);
        Assert.NotNull(gate);
        var (flags, value, active) = v.State;
        Assert.Equal(1, flags.Count(f => f));
        Assert.True(flags[v.GateIndex]);
        Assert.Equal(2f, value);
        Assert.True(active);
        Assert.Equal("set active True", log.Entries[^1]);   // active last: the component turns on fully set up
    }

    [Fact]
    public void Restore_puts_back_every_flag_the_value_and_active_exactly()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var before = v.State;
        var gate = LightGate.Raise(v, 5f)!;
        gate.SetLevel(20f);
        gate.Restore();
        var after = v.State;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Active, after.Active);
    }

    [Fact]
    public void Restore_order_is_value_then_flags_then_active()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var gate = LightGate.Raise(v, 2f)!;
        log.Entries.Clear();
        gate.Restore();
        Assert.Equal("set value 1", log.Entries[0]);
        Assert.Equal(101, log.Entries.Count(e => e.StartsWith("set flag", System.StringComparison.Ordinal)));
        Assert.Equal("set active False", log.Entries[^1]);
        Assert.Single(log.Entries, e => e.StartsWith("set active", System.StringComparison.Ordinal));   // written once, last
    }

    [Fact]
    public void Restore_runs_once_and_never_after_the_volume_is_gone()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var gate = LightGate.Raise(v, 2f)!;
        gate.Restore();
        log.Entries.Clear();
        gate.Restore();
        gate.SetLevel(3f);
        Assert.Empty(log.Entries);

        var v2 = new FakeGateVolume(log);
        var gate2 = LightGate.Raise(v2, 2f)!;
        v2.Live = false;
        log.Entries.Clear();
        gate2.Restore();
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void A_volume_without_the_gate_parameter_is_never_written()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log, gateIndex: -1);
        Assert.Null(LightGate.Raise(v, 2f));
        Assert.DoesNotContain(log.Entries, e => e.StartsWith("set", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 2f, 2f)]
    [InlineData(false, 2f, 0f)]   // no lamp on: the game's value stands
    [InlineData(true, 0f, 0f)]    // level 0: the game's value stands
    public void Policy_raises_only_with_a_lamp_on_and_a_level_above_zero(bool lampOn, float level, float expected) =>
        Assert.Equal(expected, LightGatePolicy.EffectiveLevel(new[] { (lampOn, level) }));

    [Fact]
    public void Policy_takes_the_highest_level_among_owners_with_a_lamp_on() =>
        Assert.Equal(3f, LightGatePolicy.EffectiveLevel(new[] { (true, 3f), (false, 20f), (true, 1f) }));
}
