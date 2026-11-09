namespace Stellar.Abstractions.Services;

/// <summary>
/// Theme colour facade — zero declared members. Plugins access the full colour
/// vocabulary (base, HUD, menu, status) through this single type; the split into
/// <see cref="IThemeBaseColors"/> + <see cref="IThemeHudColors"/> +
/// <see cref="IThemeMenuColors"/> + <see cref="IThemeStatusColors"/> exists so each facet stays inside the
/// STELLAR0005 8-member cap (the analyzer counts declared members per
/// interface; inherited members are unbounded).
/// </summary>
/// <remarks>
/// Same façade-inheritance pattern as <see cref="ITheme"/> itself. The split is
/// internal vocabulary — plugin code keeps using <c>theme.Colors.Accent</c> /
/// <c>theme.Colors.HudText</c> / <c>theme.Colors.MenuBackground</c> as before.
/// <para>2.30.0 added the <see cref="IThemeStatusColors"/> facet (<c>theme.Colors.Success</c> / <c>Danger</c> /
/// <c>Caution</c>). Only the framework implements this interface, so the new members can't break a plugin.</para>
/// </remarks>
public interface IThemeColors : IThemeBaseColors, IThemeHudColors, IThemeMenuColors, IThemeStatusColors
{
}
