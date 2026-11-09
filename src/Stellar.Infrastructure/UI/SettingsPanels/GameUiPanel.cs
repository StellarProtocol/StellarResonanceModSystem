using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Game UI: one "Game HUD" panel listing every allowlisted piece of the game's own HUD — name, "Moved" /
/// "Default", a Shown/Hidden chip (or a locked "Always" + "?" for pieces that may be moved but never hidden), and a
/// per-row Reset; a piece not in the current scene is a muted "Not in this scene" row. Bottom: enter / exit layout
/// editing and Reset all (with a confirm). The developer "Recon paths…" walk moved to About → Diagnostics
/// (<see cref="ReconWalk"/>, still here because it uses this panel's log).
/// </summary>
internal sealed partial class GameUiPanel
{
    private const float StatusWidth = 70f;
    private const float ResetLabel = 42f;   // a 62 px row pill
    private const float EditPillLabel = 146f;

    private readonly NativeUiService _nativeUi;
    private readonly IPluginLog _log;
    private readonly LayoutEditorService _layoutEditor;
    private readonly ILocalization _loc;
    private SettingsKit _k = null!;
    private bool _confirmReset;

    // theme is no longer read (the house kit carries the live colours); the parameter stays for the wiring.
    public GameUiPanel(NativeUiService nativeUi, ITheme theme, IPluginLog log, LayoutEditorService layoutEditor, ILocalization loc)
    {
        _nativeUi = nativeUi;
        _log = log;
        _layoutEditor = layoutEditor;
        _loc = loc;
    }

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        kit.TabChanged += () => _confirmReset = false;
        var s = kit.S;
        var rows = new List<HudElement>();
        foreach (var e in _nativeUi.Entries) rows.Add(EntryRow(e));
        var confirm = s.ConfirmPanel(new ConfirmSpec(kit.T("gameui.resetAll.q"), kit.T("gameui.resetAll.consequence"),
            kit.T("common.resetAll"), ResetAll, kit.T("common.cancel"), () => _confirmReset = false));
        var bottom = new RowElement(new HudElement[]
        {
            new ConditionalElement(() => _layoutEditor.IsEditing,
                s.CautionButton(kit.T("layout.edit.exit"), ToggleEdit, EditPillLabel),
                s.PrimaryButton(kit.T("layout.edit.enter"), ToggleEdit, EditPillLabel)),
            new SpacerElement(),
            s.NeutralButton(kit.T("common.resetAll"), () => _confirmReset = true),
        });
        return kit.Panel("gameui.panel", kit.T("gameui.panel.hint"),
            // Not FixedHeight: the list takes the panel's slack down to the bottom buttons.
            new ScrollElement(new ColumnElement(rows.ToArray(), Gap: 2f), Height: 140f),
            new ConditionalElement(() => _confirmReset, confirm, bottom));
    }

    /// <summary>Header status: "1 hidden · 3 moved".</summary>
    public string Status(SettingsKit k)
    {
        int hidden = 0, moved = 0;
        foreach (var e in _nativeUi.Entries)
        {
            if (!e.Visible) hidden++;
            else if (e.IsModified) moved++;
        }
        return _loc.TFormat("gameui.status", hidden, moved);
    }

    // One entry's row. Resolution is read LIVE through the Conditional (entries only resolve in-world, long after the
    // hub is built — a build-time IsResolved check once baked "(not present)" forever).
    private HudElement EntryRow(NativeUiService.EntryState e)
    {
        var entry = e;
        var s = _k.S;
        var id = entry.Descriptor.Id;
        var nameKey = entry.Descriptor.DisplayNameKey;
        bool Moved() => entry.IsModified && entry.Visible;
        bool IsDefault() => !entry.IsModified && entry.Visible;
        var status = new CellElement(new ConditionalElement(() => _k.Flash.IsOn(id),
            new TextElement(() => _k.Flash.Text(id), Color: () => s.Readable(s.Success(), s.Tile()), Width: StatusWidth,
                NoWrap: true, FontSize: 12) { Bold = true },
            new TextElement(() => Moved() ? _loc.T("gameui.moved") : IsDefault() ? _loc.T("gameui.default") : "",
                Color: () => Moved() ? s.Readable(s.Accent(), s.Tile()) : s.Muted(), Width: StatusWidth, NoWrap: true)),
            Width: StatusWidth);
        HudElement visibility = entry.Descriptor.SafeToHide
            ? s.Chip(() => _loc.T(entry.Visible ? "gameui.shown" : "gameui.hidden"), () => entry.Visible,
                () => SetVisible(entry, !entry.Visible))
            : new TextElement(() => _loc.T("gameui.always"), Color: () => s.Muted(), Width: StellarStyle.ChipWidth,
                Align: TextAlign.Center, NoWrap: true) { Italic = true };
        var live = s.ListTile(StellarStyle.TileRow(
                _k.LineLabel(() => _loc.T(nameKey)),
                status,
                visibility,
                s.SmallPill(_k.T("common.reset"), PillKind.Neutral, () => ResetOne(entry), ResetLabel, () => !IsDefault()),
                entry.Descriptor.SafeToHide
                    ? SettingsKit.HelpGap()
                    // One help key per entry: a shared key would light every "?" as open at once.
                    : _k.Help("gameui.always." + id, nameKey, "help.gameui.always", "tab.gameui")),
            flashing: () => _k.Flash.IsOn(id));
        var absent = StellarStyle.TileRow(
            _k.LineLabel(() => _loc.T(nameKey), () => s.Muted()),
            new TextElement(() => _loc.T("gameui.notInScene"), Color: () => s.Muted(), NoWrap: true));
        return new ConditionalElement(() => entry.IsResolved, live, absent);
    }

    private void SetVisible(NativeUiService.EntryState e, bool visible)
    {
        _nativeUi.SetVisible(e.Descriptor.Id, visible);
        var name = _loc.T(e.Descriptor.DisplayNameKey);
        _k.Flash.Flash(e.Descriptor.Id, _loc.T(visible ? "gameui.shown" : "gameui.hidden"));
        _k.Done(_loc.TFormat(visible ? "gameui.fb.shown" : "gameui.fb.hidden", name));
    }

    private void ResetOne(NativeUiService.EntryState e)
    {
        _nativeUi.ResetToOriginal(e.Descriptor.Id);
        _k.Flash.Flash(e.Descriptor.Id, _loc.T("common.reset"));
        _k.Done(_loc.TFormat("gameui.fb.reset", _loc.T(e.Descriptor.DisplayNameKey)));
    }

    private void ResetAll()
    {
        _confirmReset = false;
        _nativeUi.ResetAll();
        _k.Done(_loc.T("gameui.fb.resetAll"));
    }

    private void ToggleEdit()
    {
        _layoutEditor.ToggleEditMode();
        _k.Done(_loc.T(_layoutEditor.IsEditing ? "layout.fb.editOn" : "layout.fb.editOff"));
    }
}
