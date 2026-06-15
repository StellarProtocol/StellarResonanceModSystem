using Stellar.Abstractions.Domain.GameData;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class ImagineCooldownCalcTests
{
    [Fact]
    public void EffectiveDuration_applies_reduction_off_duration()
    {
        Assert.Equal(54000, ImagineCooldownCalc.EffectiveDuration(60000, 0, 0.10f));   // 60s -10%
        Assert.Equal(72000, ImagineCooldownCalc.EffectiveDuration(80000, 0, 0.10f));   // 80s -10%
    }

    [Fact]
    public void EffectiveDuration_prefers_valid_cd_time_when_present()
        => Assert.Equal(49500, ImagineCooldownCalc.EffectiveDuration(60000, 55000, 0.10f));   // 55s -10%

    [Fact]
    public void Second_cast_mid_recharge_queues_behind_not_restart()
    {
        // The reported bug: cast 1, wait until the 1st charge has ~300ms left, cast 2. Time-to-full must be
        // (remaining of charge 1) + perCharge = 300 + 1000 = 1300 — NOT 2*perCharge (the old reset-on-cast bug).
        var calc = new ImagineCooldownCalc();
        const int per = 1000, max = 2;

        var r0 = calc.Update(1, beginMs: 0, per, max, nowMs: 0);
        Assert.True(r0.Active); Assert.Equal(1, r0.ChargesAvailable); Assert.Equal(1000, r0.ToFullMs);

        // Cast 2 at t=700 → charge 1 has 300ms left and keeps it; charge 2 queues behind.
        var r1 = calc.Update(1, beginMs: 700, per, max, nowMs: 700);
        Assert.Equal(0, r1.ChargesAvailable);
        Assert.Equal(1300, r1.ToFullMs);   // 300 (charge1 left) + 1000 (charge2) — the fix

        // Charge 1 returns at t=1000 → 1 available, charge 2 finishes at 2000.
        var r2 = calc.Update(1, beginMs: 700, per, max, nowMs: 1000);
        Assert.True(r2.Active); Assert.Equal(1, r2.ChargesAvailable); Assert.Equal(1000, r2.ToFullMs);

        // Both back at t=2000 → full → hide.
        var r3 = calc.Update(1, beginMs: 700, per, max, nowMs: 2000);
        Assert.False(r3.Active); Assert.Equal(2, r3.ChargesAvailable);
    }
}
