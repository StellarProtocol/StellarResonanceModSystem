using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Minimal version readout + framework summary. Shown when the user opens
/// Settings → About; framework version comes from
/// <see cref="Stellar.Abstractions.Domain.FrameworkVersion.Value"/> so this
/// panel and the BepInEx plugin manifest stay in lockstep with one edit.
/// </summary>
internal sealed class AboutPanel
{
    private readonly ITheme _theme;
    private readonly ILocalization _loc;

    public AboutPanel(ITheme theme, ILocalization loc)
    {
        _theme = theme;
        _loc = loc;
    }

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration). Same content,
    /// declarative — the framework renders it as native uGUI.</summary>
    public HudElement Describe() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.TFormat("about.version", FrameworkVersion.Value)),
        new TextElement(() => _loc.T("about.tagline")),
        new SeparatorElement(),
        new TextElement(() => _loc.T("about.plugins")),
        new TextElement(() => _loc.T("about.hotkeys")),
    });

}
