using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// Exercises the sticky outcome/defeated latches added alongside the existing
/// settlement/difficulty/run-timer latches: advance-only (never downgrade a
/// resolved outcome to None), cleared on a genuinely-new run and on
/// <see cref="IDungeonStateSink.Reset"/>, but preserved across the run-id
/// drop-to-0 town transition — same stickiness rationale as <c>LastSettlement</c>.
/// </summary>
public sealed class DungeonStateServiceOutcomeTests
{
    private const long DungeonId = 148061897948659712L;
    private const long Dungeon2Id = 148061897948659713L;

    private static (IDungeonState read, IDungeonStateSink write) NewService()
    {
        var svc = new DungeonStateService();
        return (svc, svc);
    }

    [Fact]
    public void SetOutcome_Failed_thenReadFailed_andStickyThroughTownDrop()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetOutcome(2); // Failed
        Assert.Equal(DungeonOutcome.Failed, read.LastOutcome);

        write.SetCurrentRun(0); // leave to town — must NOT clear
        Assert.Equal(DungeonOutcome.Failed, read.LastOutcome);
    }

    [Fact]
    public void SetOutcome_Zero_isIgnored_and_NewRun_clears()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetOutcome(1); // Success
        write.SetOutcome(0); // None must not downgrade
        Assert.Equal(DungeonOutcome.Success, read.LastOutcome);

        write.SetCurrentRun(Dungeon2Id); // genuinely new run clears
        Assert.Equal(DungeonOutcome.None, read.LastOutcome);
    }

    [Fact]
    public void SetOutcome_LaterNonZeroWrite_OverwritesEarlierResolvedValue()
    {
        // Only 0 (None/unresolved) is guarded against; a later non-zero write
        // still wins per the reference implementation — the "never downgrade"
        // guarantee is specifically about 0 not clobbering a resolved value.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetOutcome(2); // Failed
        write.SetOutcome(1); // Success
        Assert.Equal(DungeonOutcome.Success, read.LastOutcome);
    }

    [Fact]
    public void SetDefeated_latches_and_survives_townDrop_but_clears_on_reset()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetDefeated(15);
        Assert.Equal(15, read.LastDefeatedCount);

        write.SetCurrentRun(0); // leave to town — must NOT clear
        Assert.Equal(15, read.LastDefeatedCount);

        write.Reset();
        Assert.Equal(0, read.LastDefeatedCount);
    }

    [Fact]
    public void SetDefeated_clears_on_genuinely_new_run()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetDefeated(15);

        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(0, read.LastDefeatedCount);
    }

    [Fact]
    public void Reset_ClearsOutcome()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetOutcome(2);

        write.Reset();
        Assert.Equal(DungeonOutcome.None, read.LastOutcome);
    }
}
