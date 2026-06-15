using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Game UI panel. Lists every allowlist entry with a
/// visibility checkbox + reset button. Unresolved entries are greyed out;
/// non-safe-to-hide entries (Quickbar / Player HP+MP) have their checkbox
/// disabled. The [Recon paths…] button dumps the live Canvas hierarchy to
/// the log so future patches can locate moved GameObjects.
/// </summary>
internal sealed partial class GameUiPanel
{
    private readonly NativeUiService _nativeUi;
    private readonly ITheme _theme;
    private readonly IPluginLog _log;

    public GameUiPanel(NativeUiService nativeUi, ITheme theme, IPluginLog log)
    {
        _nativeUi = nativeUi;
        _theme = theme;
        _log = log;
    }

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration). Per-entry
    /// visibility toggle (disabled for non-safe-to-hide) + Reset, in a scroll, wired to the same
    /// <see cref="NativeUiService"/>; built once on open.</summary>
    public HudElement Describe()
    {
        var rows = new System.Collections.Generic.List<HudElement>();
        foreach (var e in _nativeUi.Entries) rows.Add(EntryRow(e));
        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => "(i) Move or hide game HUD elements."),
            new ScrollElement(new ColumnElement(rows.ToArray()), 220f),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Reset all", () => _nativeUi.ResetAll()),
                new ButtonElement(() => "Recon paths…", () => ReconWalk()),
            }),
        });
    }

    // One entry's row. CRITICAL: resolution is read LIVE via a Conditional, not snapshotted at build time —
    // entries only resolve in-world (after Describe runs at startup), so a build-time `IsResolved` check baked
    // "(not present)" forever (the IMGUI panel works only because it re-reads every frame). Both branches are
    // built once; the Conditional flips to the interactive row once the element resolves in-world.
    private HudElement EntryRow(NativeUiService.EntryState e)
    {
        var entry = e;
        var safe = entry.Descriptor.SafeToHide;
        var id = entry.Descriptor.Id;
        var name = entry.Descriptor.DisplayName;
        var items = new System.Collections.Generic.List<HudElement>
        {
            new ToggleElement(() => "", () => entry.Visible, v => { if (safe) _nativeUi.SetVisible(id, v); }, () => safe),
            new TextElement(() => name),
        };
        if (!safe) items.Add(new TextElement(() => "! unsafe", () => _theme.Colors.Warning));
        items.Add(new SpacerElement());
        items.Add(new ButtonElement(() => "Reset", () => _nativeUi.ResetToOriginal(id)));
        return new ConditionalElement(() => entry.IsResolved,
            new RowElement(items.ToArray()),
            new TextElement(() => $"--- {name} (not present)", () => _theme.Colors.TextMuted));
    }

}
