using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed class CustomThemeStore : ICustomThemeStore
{
    private const string Key = "customThemes"; // Dictionary<name, presetName>

    /// <summary>The most custom themes a player can CREATE. Settings shows a fixed 3-row grid (12 cards); before this
    /// cap, theme 13+ was saved but never listed, so it could not be selected, renamed or deleted. A config that
    /// already holds more is loaded in full (user data is never dropped) — only new ones are refused.</summary>
    public const int MaxCustomThemes = 12;

    private readonly IConfigSection _config;
    private readonly Dictionary<string, ThemePreset> _themes = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    // Themes whose saved base preset this build doesn't know (e.g. "Sakura" read by a pre-2.30 framework, or a preset
    // added after this one). They load based on Default, and the ORIGINAL base string is written back on save, so a
    // later framework that knows the preset gets it back. Before 2.30 such themes were skipped at load and then
    // erased by the next save (their colour overrides orphaned).
    private readonly Dictionary<string, string> _unknownBase = new(StringComparer.Ordinal);
    // The colour overrides are keyed by theme NAME, so a rename must move them (it once didn't: the renamed
    // theme showed no colours and the old entries were orphaned). Null = no override store (unit tests of the list).
    // Must live in the SAME config file as this store's section: it stages its map and our Save writes both at once.
    private readonly IColorOverrideStore? _overrides;

    public CustomThemeStore(IConfigSection config, IColorOverrideStore? overrides = null)
    {
        _config = config;
        _overrides = overrides;
        var raw = config.Get<Dictionary<string, string>>(Key, null);
        if (raw is null) return;
        foreach (var (name, presetName) in raw)
        {
            // IsDefined too: Enum.TryParse accepts any integer string ("7") as an undefined enum value.
            if (Enum.TryParse<ThemePreset>(presetName, out var p) && Enum.IsDefined(typeof(ThemePreset), p))
                _themes[name] = p;
            else
            {
                _themes[name] = ThemePreset.Default;
                _unknownBase[name] = presetName ?? "";
            }
            _order.Add(name);
        }
    }

    public IReadOnlyList<string> Names => _order;
    public ThemePreset BasePresetOf(string name) => _themes.TryGetValue(name, out var p) ? p : ThemePreset.Default;

    /// <summary>False once <see cref="MaxCustomThemes"/> themes exist — the UI greys out "new / duplicate" on it.</summary>
    public bool CanCreate => _order.Count < MaxCustomThemes;

    public void Create(string name, ThemePreset basePreset)
    {
        name = Validate(name);
        if (_themes.ContainsKey(name)) throw new ArgumentException($"theme exists: {name}", nameof(name));
        if (!CanCreate)
            throw new ArgumentException($"custom theme limit reached ({MaxCustomThemes}): {name}", nameof(name));
        _themes[name] = basePreset; _order.Add(name);
        // A new theme starts from its base preset: drop overrides orphaned under this name by a theme deleted earlier
        // (Delete keeps them), or "Duplicate" after a delete would bring the old theme's colours back.
        _overrides?.RemoveTheme(name);
        Persist();
    }

    /// <summary>Renames a custom theme and moves everything keyed by its name with it: the base preset (and an
    /// unknown-base string) and every colour override — any stale overrides already under <paramref name="newName"/>
    /// are dropped, not merged. One config write. Case-only renames ("Blue" → "blue") are real renames (ordinal).
    /// The ACTIVE-theme pointer (<see cref="INamedTheme.ActiveCustomName"/>) is the caller's: re-point it after.</summary>
    public void Rename(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return; // no-op
        if (!_themes.TryGetValue(oldName, out var p)) return;
        newName = Validate(newName);
        if (_themes.ContainsKey(newName)) throw new ArgumentException($"theme exists: {newName}", nameof(newName));
        _themes.Remove(oldName); _order[_order.IndexOf(oldName)] = newName; _themes[newName] = p;
        if (_unknownBase.Remove(oldName, out var rawBase)) _unknownBase[newName] = rawBase;
        _overrides?.RenameTheme(oldName, newName);   // staged; the Persist below writes list + overrides together
        Persist();
    }

    /// <summary>Creates <paramref name="newName"/> as a copy of the custom theme <paramref name="source"/>: its base
    /// preset (and an unknown-base string) and every colour override, which are keyed by theme NAME — before this,
    /// "Duplicate" created the copy from the base preset alone and "Pink_copy" came out plain Default (user report
    /// 2026-11-09). Stale overrides orphaned under <paramref name="newName"/> are dropped, not merged; the source is
    /// untouched. One config write (list + overrides). The ACTIVE-theme pointer is the caller's.</summary>
    public void Duplicate(string source, string newName)
    {
        if (!_themes.TryGetValue(source, out var p)) throw new ArgumentException($"no such theme: {source}", nameof(source));
        newName = Validate(newName);
        if (_themes.ContainsKey(newName)) throw new ArgumentException($"theme exists: {newName}", nameof(newName));
        if (!CanCreate)
            throw new ArgumentException($"custom theme limit reached ({MaxCustomThemes}): {newName}", nameof(newName));
        _themes[newName] = p; _order.Add(newName);
        if (_unknownBase.TryGetValue(source, out var rawBase)) _unknownBase[newName] = rawBase;
        _overrides?.CopyTheme(source, newName);   // staged; the Persist below writes list + overrides together
        Persist();
    }

    public void Delete(string name)
    {
        if (_themes.Remove(name)) { _order.Remove(name); _unknownBase.Remove(name); Persist(); }
    }

    // Theme names are user-supplied free text used as override-store key
    // components (composite key "<theme> <slot>"). Reject empty/whitespace and
    // internal spaces (which would collide with the separator); trim the edges.
    private static string Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("theme name must be non-empty", nameof(name));
        var trimmed = name.Trim();
        if (trimmed.IndexOf(' ') >= 0)
            throw new ArgumentException("theme name must not contain spaces", nameof(name));
        if (IsBuiltInName(trimmed))
            throw new ArgumentException($"theme name is a built-in theme: {trimmed}", nameof(name));
        return trimmed;
    }

    /// <summary>True when <paramref name="name"/> is a built-in theme's name (Default, Dark, Light, Crimson, Sakura — any
    /// <see cref="ThemePreset"/>), ignoring case. A custom theme with that name read as a second "Default" next to the
    /// built-in one (user report 2026-11-09), so new and renamed themes may not use it. An EXISTING one still loads and
    /// keeps its name (user data is never dropped); only Create / Rename refuse it.</summary>
    public static bool IsBuiltInName(string name)
    {
        foreach (var p in Enum.GetNames(typeof(ThemePreset)))
            if (string.Equals(p, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Persist()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in _order) raw[n] = _unknownBase.TryGetValue(n, out var rawBase) ? rawBase : _themes[n].ToString();
        _config.Set(Key, raw); _config.Save();
    }
}
