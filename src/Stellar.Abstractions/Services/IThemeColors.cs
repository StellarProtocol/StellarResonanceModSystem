namespace Stellar.Abstractions.Services;

/// <summary>
/// Theme colour facade — zero declared members. Plugins access the full colour
/// vocabulary (base, HUD, menu) through this single type; the split into
/// <see cref="IThemeBaseColors"/> + <see cref="IThemeHudColors"/> +
/// <see cref="IThemeMenuColors"/> exists so each facet stays inside the
/// STELLAR0005 8-member cap (the analyzer counts declared members per
/// interface; inherited members are unbounded).
/// </summary>
/// <remarks>
/// Same façade-inheritance pattern as <see cref="ITheme"/> itself. The split is
/// internal vocabulary — plugin code keeps using <c>theme.Colors.Accent</c> /
/// <c>theme.Colors.HudText</c> / <c>theme.Colors.MenuBackground</c> as before.
/// </remarks>
public interface IThemeColors : IThemeBaseColors, IThemeHudColors, IThemeMenuColors
{
}
