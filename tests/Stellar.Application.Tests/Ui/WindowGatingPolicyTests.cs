using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Ui;

public sealed class WindowGatingPolicyTests
{
    private static WindowSpec Spec(bool autoHide, bool hideUntilInWorld = false) =>
        new("w", "W", new WindowRect(0, 0, 10, 10), WindowCategory.Tools, WindowPanelStyle.Custom)
        { AutoHideBehindGameMenus = autoHide, HideUntilInWorld = hideUntilInWorld };

    // Auto-hide-behind-menus gate (loggedIn = true so the login gate is inert).
    [Theory]
    [InlineData(true,  true,  true)]
    [InlineData(true,  false, false)]
    [InlineData(false, true,  false)]
    [InlineData(false, false, false)]
    public void IsDrawSuppressed_MenuGate(bool autoHide, bool menuOpen, bool expected)
        => Assert.Equal(expected, WindowGatingPolicy.IsDrawSuppressed(Spec(autoHide), menuOpen, loggedIn: true));

    // Hide-until-in-world gate (menu closed): suppressed only when opted in AND logged out.
    [Theory]
    [InlineData(true,  false, true)]
    [InlineData(true,  true,  false)]
    [InlineData(false, false, false)]
    [InlineData(false, true,  false)]
    public void IsDrawSuppressed_LoginGate(bool hideUntilInWorld, bool loggedIn, bool expected)
        => Assert.Equal(expected, WindowGatingPolicy.IsDrawSuppressed(Spec(autoHide: false, hideUntilInWorld), fullScreenMenuOpen: false, loggedIn));
}
