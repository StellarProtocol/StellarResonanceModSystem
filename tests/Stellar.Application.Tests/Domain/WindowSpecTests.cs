using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Domain;

public sealed class WindowSpecTests
{
    [Fact]
    public void StartVisible_DefaultsToTrue()
    {
        var spec = new WindowSpec("test.id", "Test",
            new WindowRect(0, 0, 100, 100),
            WindowCategory.HUD,
            WindowPanelStyle.Party);

        Assert.True(spec.StartVisible);
    }

    [Fact]
    public void StartVisible_InitializerOverrideHonoured()
    {
        var spec = new WindowSpec("test.id", "Test",
            new WindowRect(0, 0, 100, 100),
            WindowCategory.HUD,
            WindowPanelStyle.Party)
        { StartVisible = false };

        Assert.False(spec.StartVisible);
    }

    [Fact]
    public void Equality_SameFields_AreEqual()
    {
        var a = new WindowSpec("id", "T", new WindowRect(1, 2, 3, 4), WindowCategory.Tools, WindowPanelStyle.Tracker);
        var b = new WindowSpec("id", "T", new WindowRect(1, 2, 3, 4), WindowCategory.Tools, WindowPanelStyle.Tracker);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentStartVisible_AreNotEqual()
    {
        var a = new WindowSpec("id", "T", new WindowRect(0, 0, 1, 1), WindowCategory.HUD, WindowPanelStyle.Party);
        var b = new WindowSpec("id", "T", new WindowRect(0, 0, 1, 1), WindowCategory.HUD, WindowPanelStyle.Party) { StartVisible = false };
        Assert.NotEqual(a, b);
    }
}
