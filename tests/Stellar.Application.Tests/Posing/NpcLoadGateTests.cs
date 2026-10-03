using Stellar.Infrastructure.Game.Posing;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Spec § 4.4: the NPC model loads async (~250 ms); the free camera can end or the person be reset before it arrives.
// Whatever the order, a generated model is removed exactly once and never shown after a close.
public sealed class NpcLoadGateTests
{
    [Fact]
    public void Load_then_close_removes_once()
    {
        var g = new NpcLoadGate();
        Assert.Equal(NpcLoadStep.Ready, g.OnLoad());
        Assert.Equal(NpcLoadStep.Recycle, g.Close());
        Assert.Equal(NpcLoadStep.Ignore, g.Close());
    }

    [Fact]
    public void Close_before_the_load_removes_the_model_when_it_arrives()
    {
        var g = new NpcLoadGate();
        Assert.Equal(NpcLoadStep.Ignore, g.Close());
        Assert.Equal(NpcLoadStep.Recycle, g.OnLoad());
    }

    [Fact]
    public void An_error_fails_unless_already_closed()
    {
        Assert.Equal(NpcLoadStep.Fail, new NpcLoadGate().OnError());
        var closed = new NpcLoadGate();
        closed.Close();
        Assert.Equal(NpcLoadStep.Ignore, closed.OnError());
    }

    [Fact]
    public void A_second_callback_is_ignored()
    {
        var g = new NpcLoadGate();
        Assert.Equal(NpcLoadStep.Ready, g.OnLoad());
        Assert.Equal(NpcLoadStep.Ignore, g.OnLoad());
        Assert.Equal(NpcLoadStep.Ignore, g.OnError());
    }

    [Fact]
    public void Closing_after_a_failed_load_does_nothing()
    {
        var g = new NpcLoadGate();
        Assert.Equal(NpcLoadStep.Fail, g.OnError());
        Assert.Equal(NpcLoadStep.Ignore, g.Close());
    }
}
