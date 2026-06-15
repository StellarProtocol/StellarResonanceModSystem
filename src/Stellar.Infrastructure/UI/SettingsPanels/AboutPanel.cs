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

    public AboutPanel(ITheme theme)
    {
        _theme = theme;
    }

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration). Same content,
    /// declarative — the framework renders it as native uGUI.</summary>
    public HudElement Describe() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => $"Framework version: {FrameworkVersion.Value}"),
        new TextElement(() => "Dalamud-style mod framework for Blue Protocol: Star Resonance."),
        new SeparatorElement(),
        new TextElement(() => "Loaded plugins are listed in the Plugins panel."),
        new TextElement(() => "Hotkeys can be rebound in the Hotkeys panel."),
    });

}
