using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Inspector;

public sealed class ProfileCardActionRegistryTests
{
    private static ProfileCardActionSpec Spec(string id, Action<EntityId>? onClick = null) =>
        new(id, id, IconPng: null, onClick ?? (_ => { }));

    [Fact]
    public void Register_AppearsInActions()
    {
        var reg = new ProfileCardActionRegistry();
        reg.Register(Spec("inspect"));

        Assert.Single(reg.Actions);
        Assert.Equal("inspect", reg.Actions[0].Id);
    }

    [Fact]
    public void Dispose_RemovesFromActions()
    {
        var reg = new ProfileCardActionRegistry();
        var handle = reg.Register(Spec("inspect"));

        handle.Dispose();

        Assert.Empty(reg.Actions);
    }

    [Fact]
    public void MultipleRegistrations_KeepRegistrationOrder()
    {
        var reg = new ProfileCardActionRegistry();
        reg.Register(Spec("a"));
        reg.Register(Spec("b"));
        reg.Register(Spec("c"));

        Assert.Equal(new[] { "a", "b", "c" }, reg.Actions.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Register_SameId_ReplacesInPlace()
    {
        var reg = new ProfileCardActionRegistry();
        reg.Register(Spec("inspect", _ => { }));
        var replacement = Spec("inspect", _ => { });
        reg.Register(replacement);

        Assert.Single(reg.Actions);
        Assert.Same(replacement, reg.Actions[0]);
    }

    [Fact]
    public void Dispose_StaleHandleAfterReregister_IsNoOp()
    {
        var reg = new ProfileCardActionRegistry();
        var first = reg.Register(Spec("inspect"));
        reg.Register(Spec("inspect"));   // replaces the slot; `first` is now stale

        first.Dispose();                 // must NOT remove the live replacement

        Assert.Single(reg.Actions);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrowOrDoubleRemove()
    {
        var reg = new ProfileCardActionRegistry();
        reg.Register(Spec("a"));
        var handle = reg.Register(Spec("b"));

        handle.Dispose();
        handle.Dispose();

        Assert.Single(reg.Actions);
        Assert.Equal("a", reg.Actions[0].Id);
    }

    [Fact]
    public void OnClick_FiresWithEntity()
    {
        var reg = new ProfileCardActionRegistry();
        EntityId got = default;
        reg.Register(Spec("inspect", id => got = id));

        var entity = new EntityId((7L << 16) | 640L);
        reg.Actions[0].OnClick(entity);

        Assert.Equal(entity, got);
    }
}
