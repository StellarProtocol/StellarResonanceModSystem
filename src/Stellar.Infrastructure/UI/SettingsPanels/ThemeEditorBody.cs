using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Phase 9b.5 / 9c — generic custom-theme colour editor body (Settings → Themes).
/// Reads <see cref="IThemeOverrides"/> + <see cref="ICustomThemeStore"/> and
/// renders whatever is registered, with ZERO plugin-specific knowledge. Built-in
/// presets are read-only; the user clones one into a named custom theme, then
/// overrides slots per theme.
/// </summary>
/// <remarks>
/// <para>Phase 9c sparse model: the main list shows ONLY the slots this theme
/// actually overrides (mirrors the sparse override store) — a fresh clone starts
/// empty. The full registry (every plugin's colours) lives behind the
/// "+ Add colour override" picker (collapsible per-owner sections + filter +
/// multi-select), so the editor stays short no matter how many plugins register
/// slots. See the <c>.Picker</c> partial.</para>
/// <para>Persistence note: <c>SetOverride</c>/<c>ClearOverride</c> write through to
/// the config store on each call, but the editor only calls them when a slot's
/// value actually crosses an 8-bit boundary (the <see cref="Approx"/> guard) or
/// a complete hex is typed — so an idle editor performs no writes, and a slider
/// drag writes at most once per distinct 0–255 value.</para>
/// </remarks>
internal sealed partial class ThemeEditorBody
{
    private enum NameMode { None, New, Rename }

    // Framework chrome owner — its slots ("Theme.*") are a small fixed set, so they
    // ALWAYS show in the editor (Reset-to-default), unlike plugin colours which are
    // opt-in via the add-picker. Derived from the key prefix (see OwnerOf).
    private const string SystemOwner = "Theme";

    private readonly INamedTheme _namedTheme;
    private readonly ICustomThemeStore _store;
    private readonly IThemeOverrides _overrides;
    private readonly ITheme _theme;

    // Editor state.
    private string _nameBuffer = "";
    private NameMode _nameMode = NameMode.None;
    private string _renameTarget = "";
    private string? _nameError;
    private string? _expandedSlotKey;
    private string _hexBuffer = "";
    private bool _confirmDelete;
    // Set when an override is edited; flushed (→ texture rebuild) on mouse-release
    // so a slider drag triggers one rebuild instead of one per frame.
    private bool _editDirty;

    public ThemeEditorBody(INamedTheme namedTheme, ICustomThemeStore store,
                           IThemeOverrides overrides, ITheme theme)
    {
        _namedTheme = namedTheme;
        _store = store;
        _overrides = overrides;
        _theme = theme;
    }

    // A slider drag holds the mouse down and fires many SetOverride calls; the
    // chrome's baked textures (accent fill, dividers, GlassMenu gradient, …)
    // only rebuild on INamedTheme.ActiveChanged. Coalesce: notify once the
    // button is released so the drag yields a single rebuild, not one per frame.
    private void FlushEditsOnRelease()
    {
        if (_editDirty && !Input.GetMouseButton(0))
        {
            _overrides.Flush();             // persist once on release (not per drag frame)
            _namedTheme.NotifyColorsChanged();
            _editDirty = false;
        }
    }

    private void ToggleExpand(string key, ColorRgba c)
    {
        if (_expandedSlotKey == key) { _expandedSlotKey = null; return; }
        _expandedSlotKey = key;
        _hexBuffer = ToHex(c);
    }

    private void EnterNameMode(NameMode mode, string seed)
    {
        _nameMode = mode;
        _nameBuffer = seed;
        _nameError = null;
        _confirmDelete = false;
    }

    private void CancelNameMode()
    {
        _nameMode = NameMode.None;
        _nameBuffer = "";
        _nameError = null;
    }

    private void TryCommitName()
    {
        var name = (_nameBuffer ?? "").Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOf(' ') >= 0)
        {
            _nameError = "Name must be non-empty and contain no spaces";
            return;
        }
        if (NameExists(name)) { _nameError = "That name is taken"; return; }
        if (_nameMode == NameMode.New)
        {
            var basePreset = BaseFromActive();
            _store.Create(name, basePreset);
            _namedTheme.SetActiveCustom(name, basePreset);
        }
        else
        {
            var basePreset = _store.BasePresetOf(_renameTarget);
            _store.Rename(_renameTarget, name);
            if (_namedTheme.ActiveCustomName == _renameTarget) _namedTheme.SetActiveCustom(name, basePreset);
        }
        CancelNameMode();
    }

    private bool NameExists(string name)
    {
        foreach (var n in _store.Names) if (n == name) return true;
        return false;
    }

    private ThemePreset BaseFromActive()
        => _namedTheme.ActiveCustomName is { } c ? _store.BasePresetOf(c) : _namedTheme.Active;

    private static bool Approx(float a, float b) => Mathf.Abs(a - b) < 0.5f / 255f;

    // #RRGGBB when fully opaque; #RRGGBBAA when the colour has transparency.
    private static string ToHex(ColorRgba c)
    {
        var rgb = $"#{Clamp255(c.R):X2}{Clamp255(c.G):X2}{Clamp255(c.B):X2}";
        var a = Clamp255(c.A);
        return a == 255 ? rgb : rgb + a.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static int Clamp255(float f) => Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

    private static bool TryParseHex(string s, out ColorRgba c)
    {
        c = default;
        if (string.IsNullOrEmpty(s)) return false;
        var h = s.TrimStart('#');
        if (h.Length != 6 && h.Length != 8) return false;
        if (!TryByte(h, 0, out var r) || !TryByte(h, 2, out var g) || !TryByte(h, 4, out var b)) return false;
        var a = 255;
        if (h.Length == 8 && !TryByte(h, 6, out a)) return false;
        c = new ColorRgba(r / 255f, g / 255f, b / 255f, a / 255f);
        return true;
    }

    private static bool TryByte(string h, int start, out int value)
        => int.TryParse(h.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

}
