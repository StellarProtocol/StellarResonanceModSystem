using System;
using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Domain;

public sealed class WindowSpecTests
{
    // ShouldRender participates in record value-equality (it's a Func, compared by reference), so the
    // equality tests share one delegate instance; behavioural tests only need any predicate.
    private static readonly Func<bool> AlwaysRender = () => true;

    [Fact]
    public void StartVisible_DefaultsToTrue()
    {
        var spec = new WindowSpec("test.id", "Test",
            new WindowRect(0, 0, 100, 100),
            WindowCategory.HUD,
            WindowPanelStyle.Party) { ShouldRender = AlwaysRender };

        Assert.True(spec.StartVisible);
    }

    [Fact]
    public void StartVisible_InitializerOverrideHonoured()
    {
        var spec = new WindowSpec("test.id", "Test",
            new WindowRect(0, 0, 100, 100),
            WindowCategory.HUD,
            WindowPanelStyle.Party)
        { ShouldRender = AlwaysRender, StartVisible = false };

        Assert.False(spec.StartVisible);
    }

    [Fact]
    public void Equality_SameFields_AreEqual()
    {
        var a = new WindowSpec("id", "T", new WindowRect(1, 2, 3, 4), WindowCategory.Tools, WindowPanelStyle.Tracker) { ShouldRender = AlwaysRender };
        var b = new WindowSpec("id", "T", new WindowRect(1, 2, 3, 4), WindowCategory.Tools, WindowPanelStyle.Tracker) { ShouldRender = AlwaysRender };
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentStartVisible_AreNotEqual()
    {
        var a = new WindowSpec("id", "T", new WindowRect(0, 0, 1, 1), WindowCategory.HUD, WindowPanelStyle.Party) { ShouldRender = AlwaysRender };
        var b = new WindowSpec("id", "T", new WindowRect(0, 0, 1, 1), WindowCategory.HUD, WindowPanelStyle.Party) { ShouldRender = AlwaysRender, StartVisible = false };
        Assert.NotEqual(a, b);
    }
}
