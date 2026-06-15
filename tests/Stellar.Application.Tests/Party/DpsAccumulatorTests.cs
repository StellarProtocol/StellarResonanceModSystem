using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Party;

public sealed class DpsAccumulatorTests
{
    [Fact]
    public void Live_OneSecond_OneHit_ProducesAmountPerSecond()
    {
        var acc = new DpsAccumulator();
        acc.RecordDamage(1000, 5000);

        // Encounter-average semantic: total / max(1s, last - first).
        // A single 5000-damage hit: total=5000, span=max(1000, 0)=1000 -> Live = 5000.
        Assert.Equal(5000L, acc.Live);
    }

    [Fact]
    public void Live_FreezesAtLastComputedValueWhenNoFurtherEvents()
    {
        // After Stage 5: there is no idle decay. Live is recomputed only when
        // a damage event arrives, so the displayed DPS stays frozen at the
        // last value computed during the last RecordDamage call.
        var acc = new DpsAccumulator();
        acc.RecordDamage(1000, 5000);
        long frozen = acc.Live;

        Assert.Equal(5000L, frozen);
        Assert.Equal(frozen, acc.Live); // still the same — no Tick to slide it
    }

    [Fact]
    public void Encounter_AccumulatesUntilIdleResets()
    {
        var acc = new DpsAccumulator();
        acc.RecordDamage(1000, 1000);
        acc.RecordDamage(2000, 1000);
        acc.RecordDamage(3000, 1000);
        Assert.True(acc.Encounter > 0);

        // 31s of idle (past 30s reset).
        acc.RecordDamage(34000, 500);
        // New encounter just started; total = 500, span = max(1s, 0) = 1s -> 500.
        Assert.Equal(500L, acc.Encounter);
    }

    [Fact]
    public void Live_TwoHitsTenSecondsApart_IsEncounterAverage()
    {
        var acc = new DpsAccumulator();
        acc.RecordDamage(timestampMs: 1000,  amount: 1000);
        acc.RecordDamage(timestampMs: 11000, amount: 1000);
        // total = 2000; span = 11000 - 1000 = 10000 ms; rate = 2000 * 1000 / 10000 = 200.
        Assert.Equal(200L, acc.Live);
        Assert.Equal(200L, acc.Encounter);
    }
}
