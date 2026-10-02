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

    // Portrait-capture review I-1/I-2 (2026-10-03): a window takes input and is padded unless it opts into Passive —
    // the frame guide's Passive=true is the only way a full-screen layer stays click-through and lines up exactly.
    [Xunit.Fact]
    public void Passive_defaults_to_false()
    {
        var spec = new WindowSpec("t", "t", new WindowRect(0f, 0f, 10f, 10f), WindowCategory.HUD, WindowPanelStyle.Borderless)
        { ShouldRender = () => true };
        Xunit.Assert.False(spec.Passive);
        Xunit.Assert.True((spec with { Passive = true }).Passive);
    }
}
