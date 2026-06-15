using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed class CustomThemeStore : ICustomThemeStore
{
    private const string Key = "customThemes"; // Dictionary<name, presetName>
    private readonly IConfigSection _config;
    private readonly Dictionary<string, ThemePreset> _themes = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public CustomThemeStore(IConfigSection config)
    {
        _config = config;
        var raw = config.Get<Dictionary<string, string>>(Key, null);
        if (raw is not null)
            foreach (var (name, presetName) in raw)
                if (Enum.TryParse<ThemePreset>(presetName, out var p)) { _themes[name] = p; _order.Add(name); }
    }

    public IReadOnlyList<string> Names => _order;
    public ThemePreset BasePresetOf(string name) => _themes.TryGetValue(name, out var p) ? p : ThemePreset.Default;

    public void Create(string name, ThemePreset basePreset)
    {
        name = Validate(name);
        if (_themes.ContainsKey(name)) throw new ArgumentException($"theme exists: {name}", nameof(name));
        _themes[name] = basePreset; _order.Add(name); Persist();
    }

    public void Rename(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return; // no-op
        if (!_themes.TryGetValue(oldName, out var p)) return;
        newName = Validate(newName);
        if (_themes.ContainsKey(newName)) throw new ArgumentException($"theme exists: {newName}", nameof(newName));
        _themes.Remove(oldName); _order[_order.IndexOf(oldName)] = newName; _themes[newName] = p; Persist();
    }

    public void Delete(string name)
    {
        if (_themes.Remove(name)) { _order.Remove(name); Persist(); }
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
        return trimmed;
    }

    private void Persist()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in _order) raw[n] = _themes[n].ToString();
        _config.Set(Key, raw); _config.Save();
    }
}
