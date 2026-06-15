using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Capture;
using Xunit;

namespace Stellar.Application.Tests.Wire.Capture;

public sealed class CaptureFilterTests
{
    [Fact]
    public void TeamPreset_AllowsGrpcTeamNtfAnyKind_AndWorldReturns()
    {
        var f = CaptureFilter.Parse("team");
        Assert.True(f.Enabled);
        Assert.True(f.Allows(966773353, 2, WireMessageKind.Notify));
        Assert.True(f.Allows(103198054, 311327, WireMessageKind.Return));
        Assert.False(f.Allows(103198054, 311327, WireMessageKind.Notify));
    }

    [Fact]
    public void All_AllowsEverything()
    {
        var f = CaptureFilter.Parse("all");
        Assert.True(f.Allows(1, 1, WireMessageKind.Call));
        Assert.True(f.Allows(999, 0, WireMessageKind.Return));
    }

    [Fact]
    public void ExplicitTerms_MatchByAnyTerm()
    {
        var f = CaptureFilter.Parse("svc:966773353,svc:103198054:Return,kind:Notify");
        Assert.True(f.Allows(966773353, 5, WireMessageKind.Call));
        Assert.True(f.Allows(103198054, 0, WireMessageKind.Return));
        Assert.False(f.Allows(103198054, 0, WireMessageKind.Call));
        Assert.True(f.Allows(42, 0, WireMessageKind.Notify));
    }

    [Fact]
    public void NullOrEmpty_Disabled()
    {
        Assert.False(CaptureFilter.Parse(null).Enabled);
        Assert.False(CaptureFilter.Parse("").Enabled);
    }

    [Fact]
    public void Garbled_DisabledWithReason()
    {
        var f = CaptureFilter.Parse("svc:notanumber");
        Assert.False(f.Enabled);
        Assert.NotNull(f.Error);
    }
}
