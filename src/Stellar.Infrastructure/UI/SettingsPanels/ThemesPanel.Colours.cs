using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// Theme actions (select a card, duplicate, rename, delete) and the "Edit colours" CHILD window: a house-style GlassMenu
// window (ThemeEditorBody.Colours.cs) opened centred on the hub and in front. Front ALWAYS on open: a show from hidden
// lands on top by itself (non-sticky WindowStacking), but "Edit colours" pressed while the child is ALREADY open (and
// the hub was activated since) needs the explicit front (CLAUDE.md Rule 5 exception). The first SetRect after a show is ignored
// until the window is built (it restores its saved position), so the centring is re-applied for a short burst.
// Save / Discard (ThemeEditorBody.Session.cs): every action that would close or retarget the dialog while it holds
// unsaved changes goes through GuardUnsaved (ask first); only a close that cannot be held keeps what's shown (saves).
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

    /// <summary>Registers the (hidden) colour-editor child window and its colour-picker child window. Call once after
    /// the hub (<paramref name="hub"/>) is registered; <paramref name="gate"/> = the hub's own render gate;
    /// <paramref name="framework"/> = screen/canvas size for docking the picker.</summary>
    public IWindowControl RegisterColoursWindow(IWindowHost windows, SettingsKit kit, Func<bool> gate, Func<IWindowControl?> hub,
        IFramework framework)
    {
        _hub = hub;
        _fw = framework;
        _colours = windows.Register(new WindowRegistration(
            new WindowSpec(ColoursWindowId, "Stellar Settings: Edit colours", new WindowRect(700f, 220f, ColoursWidth, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            {
                ShouldRender = gate, Closable = true, Draggable = true, StartVisible = false,
                IsolateCanvas = true,   // own batching canvas: its picker list scroll / drag re-snaps only this window
                TitleProvider = () => _text.T("colours.window.title"),
            },
            _editor.DescribeColours(kit),
            OnClose: RequestCloseColours));
        RegisterPickerWindow(windows, kit, gate);
        return _colours;
    }

    private void OpenColours()
    {
        if (_colours == null || !IsCustomActive) return;
        // The Save / Discard baseline is taken on a FRESH open only: "Edit colours" pressed while it is already open
        // just fronts it and keeps the unsaved edits counted against the original baseline.
        if (!_coloursOpen) _editor.BeginSession();
        _colours.SetVisible(true);
        _colours.BringToFront();
        _coloursOpen = true;
        _centreTicks = CentreTicks;
    }

    private void CloseColours()
    {
        ClosePicker();   // never left orphaned (ThemesPanel.Picker.cs)
        _coloursOpen = false;
        _centreTicks = 0;
        _held = null;
        _editor.EndSession();
        _colours?.SetVisible(false);
    }

    // The dialog's title-bar ✕: closes at once with nothing unsaved, otherwise asks first (Save / Discard close it).
    private void RequestCloseColours() => GuardUnsaved(CloseColours);

    /// <summary>"Ask first" for anything that would close or retarget the open Edit colours dialog while it holds
    /// unsaved changes (its ✕, the hub's ✕, switching / duplicating / deleting the theme): the action is HELD, the
    /// dialog's footer shows the "Unsaved changes" confirm and the dialog comes to the front; Save or Discard then runs
    /// the action, Keep editing drops it. Nothing unsaved (or the dialog closed) → the action runs right away.</summary>
    internal void GuardUnsaved(Action action)
    {
        if (!_coloursOpen || _colours == null) { action(); return; }
        _editor.TickSession();   // count NOW: a click can land before this tick's recount
        if (!_editor.HasUnsaved) { action(); return; }
        _held = action;
        _editor.AskUnsaved(ResolveHeld);
        // Already visible, and the hub / a theme card was just clicked (hub activated since) → explicit front
        // (CLAUDE.md Rule 5 exception; the dialog is fronted on every open, so ZFront stays consistent).
        _colours.BringToFront();
    }

    private Action? _held;

    // Save (true) / Discard (false) run the held action; Keep editing (null) drops it.
    private void ResolveHeld(bool? saved)
    {
        var a = _held;
        _held = null;
        if (saved is not { } didSave || a == null) return;
        // Hub footer feedback for the held paths (the dialog itself may be closing); the action's own feedback
        // ("Theme set to …", "Deleted …") replaces it when it has one.
        _k.Done(_text.T(didSave ? "colours.fb.saved" : "colours.fb.discarded"));
        a();
    }

    /// <summary>Hub tick: the open-centred burst, and closing the child with its hub (or when no custom theme is active
    /// any more — e.g. it was deleted).</summary>
    public void TickColoursWindow()
    {
        if (!_coloursOpen || _colours == null) return;
        var hub = _hub();
        // Closed by something that could not be held (the hub hidden by anything but its own ✕ — that one is guarded —,
        // logout / loading, unload): KEEP what's shown (persist it), so nothing the player saw is silently lost.
        if (hub == null || !hub.IsShown || !IsCustomActive)
        {
            if (_editor.SessionTheme != null && _namedTheme.ActiveCustomName == _editor.SessionTheme) _editor.SaveSession();
            CloseColours();
            return;
        }
        // The active custom theme changed under the open dialog (a guarded switch / duplicate already resolved its
        // changes; a rename carries the baseline over itself) → the dialog now edits that theme: new baseline.
        if (_namedTheme.ActiveCustomName != _editor.SessionTheme) { _held = null; _editor.BeginSession(); }
        _editor.TickSession();
        if (!_editor.IsAsking) _held = null;   // the confirm went away (e.g. the changes were reset by hand)
        TickPicker();   // open / re-target / follow / close the docked picker (ThemesPanel.Picker.cs)
        if (_centreTicks <= 0) return;
        var c = _colours.Rect;
        if (!_colours.IsShown || c.Width <= 1f || c.Height <= 1f) return;   // not built / laid out yet
        _centreTicks--;
        var h = hub.Rect;
        _colours.SetRect(new WindowRect(MathF.Max(0f, h.X + (h.Width - c.Width) / 2f),
            MathF.Max(0f, h.Y + (h.Height - c.Height) / 2f), c.Width, c.Height));
    }

    // A theme switch would retarget the open Edit colours dialog → GuardUnsaved (asks first when it holds changes).
    // A click on the card that is already active changes nothing, so it never asks.
    private void SelectCard(int idx)
    {
        _confirmDelete = false;
        _editor.CancelRename();
        if (idx < Presets.Length)
        {
            var p = Presets[idx];
            if (_namedTheme.ActiveCustomName == null && _namedTheme.Active == p) return;
            GuardUnsaved(() => { _namedTheme.SetActive(p); SelectedFeedback(); });
        }
        else
        {
            var n = idx - Presets.Length;
            if (n >= _store.Names.Count) return;
            var name = _store.Names[n];
            if (_namedTheme.ActiveCustomName == name) return;
            GuardUnsaved(() => { _editor.SelectCustom(name); SelectedFeedback(); });
        }
    }

    private void SelectedFeedback() => _k.Done(_text.TFormat("themes.fb.selected", ActiveThemeName()));

    // Duplicate makes the copy the ACTIVE theme (retargets the dialog) → asks first; at the limit nothing switches.
    private void Duplicate()
    {
        _confirmDelete = false;
        if (!_store.CanCreate) { _k.Done(_text.TFormat("themes.fb.limit", CustomThemeLimit)); return; }
        GuardUnsaved(() =>
        {
            if (_editor.DuplicateActive() is { } name) _k.Done(_text.TFormat("themes.fb.created", name));
            else _k.Done(_text.TFormat("themes.fb.limit", CustomThemeLimit));
        });
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

    // Delete closes the open Edit colours dialog (no custom theme is active after it) → asks first. The hub's delete
    // confirm folds away either way: the dialog's confirm takes over, and Keep editing cancels the delete.
    private void ConfirmDelete()
    {
        _confirmDelete = false;
        GuardUnsaved(() => { if (_editor.DeleteActive() is { } name) _k.Done(_text.TFormat("themes.fb.deleted", name)); });
    }

    private void ResetTransient()
    {
        _confirmDelete = false;
        _editor.CancelRename();
    }
}
