using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Ui;

public sealed class WindowGatingPolicyTests
{
    private static WindowSpec Spec(bool render) =>
        new("w", "W", new WindowRect(0, 0, 10, 10), WindowCategory.Tools, WindowPanelStyle.Custom)
        { ShouldRender = () => render };

    // Visibility is now purely the plugin-owned ShouldRender predicate: hide = !ShouldRender().
    [Theory]
    [InlineData(true, false)]   // predicate says draw → not suppressed
    [InlineData(false, true)]   // predicate says hide → suppressed
    public void IsDrawSuppressed_MirrorsShouldRender(bool render, bool expectedSuppressed)
        => Assert.Equal(expectedSuppressed, WindowGatingPolicy.IsDrawSuppressed(Spec(render)));
}
