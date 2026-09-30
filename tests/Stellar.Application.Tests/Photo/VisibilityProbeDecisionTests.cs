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
}
