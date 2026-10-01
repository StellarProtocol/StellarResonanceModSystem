using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class ReassertGateTests
{
    [Fact]
    public void Unarmed_gate_never_fires()
    {
        Assert.False(new ReassertGate().TryTake(ready: true));
    }

    [Fact]
    public void Armed_gate_waits_for_ready_then_fires_once()
    {
        var g = new ReassertGate();
        g.Request();
        Assert.False(g.TryTake(ready: false));
        Assert.True(g.IsPending);
        Assert.True(g.TryTake(ready: true));
        Assert.False(g.TryTake(ready: true));
    }
}
