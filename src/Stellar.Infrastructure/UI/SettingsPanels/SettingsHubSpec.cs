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
            new WindowRect(880f, 414f, SettingsHub.WindowWidth, SettingsHub.WindowHeight), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
        {
            ShouldRender = shouldRender, Closable = true, Draggable = true, StartVisible = false,
            // Fixed 800 × 612 on EVERY tab: the body strut (SettingsHub.BodyHeight) fills it exactly, and a tab whose
            // content runs taller (Performance at a larger font scale / a long locale) squeezes its list instead of
            // growing the window. Before, the height was content-fit, so one over-budget tab resized the hub.
            FixedHeight = true,
            // Own batching canvas: tab scroll / drag re-snaps only the hub (local pixelPerfect), not every Stellar window.
            IsolateCanvas = true,
            TitleProvider = () => loc.T(TitleKey),
        };
}
