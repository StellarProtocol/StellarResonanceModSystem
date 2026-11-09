using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// Theme actions (select a card, duplicate, rename, delete) and the "Edit colours" CHILD window: a house-style GlassMenu
// window (ThemeEditorBody.Colours.cs) opened centred on the hub and in front. Front ALWAYS on open: ZFront is sticky
// (once the hub or the help popover has been fronted, a never-fronted child opening from hidden can land under them —
// CLAUDE.md Rule 5 exception, WindowBuilder-Patterns "ZFront is STICKY"). The first SetRect after a show is ignored
// until the window is built (it restores its saved position), so the centring is re-applied for a short burst.
internal sealed partial class ThemesPanel
{
    internal const string ColoursWindowId = "stellar.settings.colours";
    private const float ColoursWidth = 440f;
    private const int CentreTicks = 30;

    private IWindowControl? _colours;
    private Func<IWindowControl?> _hub = () => null;
    private bool _coloursOpen;
    private int _centreTicks;
    private int _renameFocus;

    /// <summary>Registers the (hidden) colour-editor child window. Call once after the hub (<paramref name="hub"/>) is
    /// registered; <paramref name="gate"/> = the hub's own render gate.</summary>
    public IWindowControl RegisterColoursWindow(IWindowHost windows, SettingsKit kit, Func<bool> gate, Func<IWindowControl?> hub)
    {
        _hub = hub;
        _colours = windows.Register(new WindowRegistration(
            new WindowSpec(ColoursWindowId, "Stellar Settings: Edit colours", new WindowRect(700f, 220f, ColoursWidth, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            {
                ShouldRender = gate, Closable = true, Draggable = true, StartVisible = false,
                TitleProvider = () => _text.T("colours.window.title"),
            },
            _editor.DescribeColours(kit, CloseColours),
            OnClose: CloseColours));
        return _colours;
    }

    private void OpenColours()
    {
        if (_colours == null || !IsCustomActive) return;
        _colours.SetVisible(true);
        _colours.BringToFront();
        _coloursOpen = true;
        _centreTicks = CentreTicks;
    }

    private void CloseColours()
    {
        _coloursOpen = false;
        _centreTicks = 0;
        _colours?.SetVisible(false);
    }

    /// <summary>Hub tick: the open-centred burst, and closing the child with its hub (or when no custom theme is active
    /// any more — e.g. it was deleted).</summary>
    public void TickColoursWindow()
    {
        if (!_coloursOpen || _colours == null) return;
        var hub = _hub();
        if (hub == null || !hub.IsShown || !IsCustomActive) { CloseColours(); return; }
        if (_centreTicks <= 0) return;
        var c = _colours.Rect;
        if (!_colours.IsShown || c.Width <= 1f || c.Height <= 1f) return;   // not built / laid out yet
        _centreTicks--;
        var h = hub.Rect;
        _colours.SetRect(new WindowRect(MathF.Max(0f, h.X + (h.Width - c.Width) / 2f),
            MathF.Max(0f, h.Y + (h.Height - c.Height) / 2f), c.Width, c.Height));
    }

    private void SelectCard(int idx)
    {
        _confirmDelete = false;
        _editor.CancelRename();
        if (idx < Presets.Length)
        {
            var p = Presets[idx];
            if (_namedTheme.ActiveCustomName == null && _namedTheme.Active == p) return;
            _namedTheme.SetActive(p);
        }
        else
        {
            var n = idx - Presets.Length;
            if (n >= _store.Names.Count) return;
            var name = _store.Names[n];
            if (_namedTheme.ActiveCustomName == name) return;
            _editor.SelectCustom(name);
        }
        _k.Done(_text.TFormat("themes.fb.selected", ActiveThemeName()));
    }

    private void Duplicate()
    {
        _confirmDelete = false;
        if (_editor.DuplicateActive() is { } name) _k.Done(_text.TFormat("themes.fb.created", name));
        else _k.Done(_text.TFormat("themes.fb.limit", CustomThemeLimit));
    }

    private void BeginRename()
    {
        _confirmDelete = false;
        _editor.BeginRename();
        _renameFocus++;
    }

    private void CommitRename()
    {
        if (!_editor.IsRenaming) return;   // Enter after Save already closed the field
        if (_editor.CommitRename() is { } name) _k.Done(_text.TFormat("themes.fb.renamed", name));
    }

    private void ConfirmDelete()
    {
        _confirmDelete = false;
        if (_editor.DeleteActive() is { } name) _k.Done(_text.TFormat("themes.fb.deleted", name));
    }

    private void ResetTransient()
    {
        _confirmDelete = false;
        _editor.CancelRename();
    }
}
