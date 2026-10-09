using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// The Settings hub's <see cref="WindowSpec"/>. Built during wiring (<c>OnHotUpdateReady</c>, before
/// <c>Game.Init</c>), when the client language must NOT be read — a first read that early can latch the wrong
/// language index for the session. So the title is the English fallback plus a <see cref="WindowSpec.TitleProvider"/>
/// the window chrome evaluates only when it builds the (initially hidden) hub and again on a language change.
/// Pure data, so a test can pin "constructing this performs no T()".
/// </summary>
internal static class SettingsHubSpec
{
    internal const string Id = "stellar.settings.ugui";
    internal const string FallbackTitle = "Stellar Settings";   // == en.json settings.window.title
    internal const string TitleKey = "settings.window.title";

    public static WindowSpec Create(ILocalization loc, Func<bool> shouldRender)
        => new(Id, FallbackTitle,
            new WindowRect(1591f, 722f, 600f, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)   // wide enough for Hotkeys rows
        {
            ShouldRender = shouldRender, Closable = true, Draggable = true, StartVisible = false,
            TitleProvider = () => loc.T(TitleKey),
        };
}
