using Stellar.Application.Abstractions;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class VisibilityProbeDecisionTests
{
    [Fact]
    public void Type_not_loaded_is_optimistic_regardless_of_member_found()
    {
        Assert.Equal(ProbeOutcome.Optimistic, VisibilityProbeDecision.Decide(typeLoaded: false, memberFound: false));
        Assert.Equal(ProbeOutcome.Optimistic, VisibilityProbeDecision.Decide(typeLoaded: false, memberFound: true));
    }

    [Fact]
    public void Type_loaded_and_member_found_is_available()
    {
        Assert.Equal(ProbeOutcome.Available, VisibilityProbeDecision.Decide(typeLoaded: true, memberFound: true));
    }

    [Fact]
    public void Type_loaded_and_member_missing_is_definitively_unavailable()
    {
        Assert.Equal(ProbeOutcome.DefinitivelyUnavailable, VisibilityProbeDecision.Decide(typeLoaded: true, memberFound: false));
    }

    // Photo Studio fw fix round (qa/perf): after a game patch renames/removes a hot-update type, FindType stays null
    // forever — once hot-update is READY that is a definitive negative (TTL-cached), not an optimistic re-probe per call.
    [Fact]
    public void Type_missing_after_hot_update_ready_is_definitively_unavailable()
    {
        Assert.Equal(ProbeOutcome.DefinitivelyUnavailable, VisibilityProbeDecision.Decide(typeLoaded: false, memberFound: false, hotUpdateReady: true));
    }

    [Fact]
    public void Type_missing_before_hot_update_ready_stays_optimistic() =>
        Assert.Equal(ProbeOutcome.Optimistic, VisibilityProbeDecision.Decide(typeLoaded: false, memberFound: false, hotUpdateReady: false));

    [Fact]
    public void Hot_update_ready_does_not_change_a_loaded_type_verdict()
    {
        Assert.Equal(ProbeOutcome.Available, VisibilityProbeDecision.Decide(typeLoaded: true, memberFound: true, hotUpdateReady: true));
        Assert.Equal(ProbeOutcome.DefinitivelyUnavailable, VisibilityProbeDecision.Decide(typeLoaded: true, memberFound: false, hotUpdateReady: true));
    }
}
