using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// Custom-theme management for the Themes tab's cards (ThemesPanel.Cards.cs): select, duplicate (immediately, with a
// generated name — the prototype's "+ Duplicate"), rename (the inline field) and delete (after the panel's confirm).
// Names keep the store's rule: non-empty, no spaces, unique.
internal sealed partial class ThemeEditorBody
{
    internal string NameBuffer { get => _nameBuffer; set => _nameBuffer = value ?? ""; }
    internal string? NameError => _nameError;
    internal bool IsRenaming => _nameMode == NameMode.Rename;

    internal void SelectCustom(string name)
    {
        _namedTheme.SetActiveCustom(name, _store.BasePresetOf(name));
        _expandedSlotKey = null;
        CancelNameMode();
    }

    /// <summary>Creates a new custom theme on the active theme's base preset (colour overrides are not copied — the
    /// store creates a theme from its base, as the old "Duplicate" did) under "&lt;name&gt;_copy" (then _copy2, _copy3, …) and makes it
    /// active. Null when the 12-theme limit is reached.</summary>
    internal string? DuplicateActive()
    {
        if (!_store.CanCreate) return null;
        CancelNameMode();
        var seed = _namedTheme.ActiveCustomName ?? _namedTheme.Active.ToString();
        var name = seed + "_copy";
        for (var n = 2; NameExists(name); n++) name = seed + "_copy" + n;
        var basePreset = BaseFromActive();
        _store.Create(name, basePreset);
        _namedTheme.SetActiveCustom(name, basePreset);
        return name;
    }

    internal void BeginRename()
    {
        var a = _namedTheme.ActiveCustomName;
        if (a == null) return;
        EnterNameMode(NameMode.Rename, a);
        _renameTarget = a;
    }

    internal void CancelRename() => CancelNameMode();

    /// <summary>Applies the rename field; returns the new name, or null (field stays open with <see cref="NameError"/>
    /// set) when the name is empty, has a space or is taken. An unchanged name just closes the field.</summary>
    internal string? CommitRename()
    {
        var name = (_nameBuffer ?? "").Trim();
        if (name == _renameTarget) { CancelNameMode(); return null; }
        if (name.Length == 0 || name.IndexOf(' ') >= 0) { _nameError = _text.T("themes.nameError.empty"); return null; }
        if (NameExists(name)) { _nameError = _text.T("themes.nameError.taken"); return null; }
        var basePreset = _store.BasePresetOf(_renameTarget);
        _store.Rename(_renameTarget, name);
        if (_namedTheme.ActiveCustomName == _renameTarget) _namedTheme.SetActiveCustom(name, basePreset);
        CancelNameMode();
        return name;
    }

    /// <summary>Deletes the active custom theme and falls back to its base preset; returns its name (null when a
    /// built-in is active).</summary>
    internal string? DeleteActive()
    {
        var active = _namedTheme.ActiveCustomName;
        if (active == null) return null;
        var b = _store.BasePresetOf(active);
        _store.Delete(active);
        _namedTheme.SetActive(b);
        _expandedSlotKey = null;
        CancelNameMode();
        return active;
    }
}
