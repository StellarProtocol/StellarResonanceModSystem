// src/Stellar.Application/Services/ColorOverrideStore.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class ColorOverrideStore : IColorOverrideStore
{
    private const string MapKey = "overrides"; // persisted as Dictionary<string,string> "<theme> <slot>" -> "#RRGGBBAA"
    private const char Sep = ' ';
    private readonly IConfigSection _config;

    // In-memory map keyed by a (theme, slot) value-tuple holding the parsed
    // colour. Avoids composing a lookup string + reparsing hex on every
    // Has/TryGet — those run per-slot per-frame while the theme editor is open.
    // The on-disk format is unchanged: a flat "<theme> <slot>" -> hex dictionary,
    // (de)composed only at load and Flush.
    private readonly Dictionary<(string Theme, string Slot), ColorRgba> _map = new();
    private bool _dirty;

    public ColorOverrideStore(IConfigSection config)
    {
        _config = config;
        var raw = config.Get<Dictionary<string, string>>(MapKey, null);
        if (raw == null) return;
        foreach (var kv in raw)
            if (TrySplit(kv.Key, out var theme, out var slot) && TryParseHex(kv.Value, out var c))
                _map[(theme, slot)] = c;
    }

    public bool TryGet(string themeName, string slotKey, out ColorRgba value)
        => _map.TryGetValue((themeName, slotKey), out value);

    public void Set(string themeName, string slotKey, ColorRgba value)
    {
        _map[(themeName, slotKey)] = value;
        _dirty = true;
    }

    public void Clear(string themeName, string slotKey)
    {
        if (_map.Remove((themeName, slotKey))) _dirty = true;
    }

    public bool Has(string themeName, string slotKey) => _map.ContainsKey((themeName, slotKey));

    public void Flush()
    {
        if (!_dirty) return;
        Stage();
        _config.Save();
    }

    public bool RenameTheme(string oldName, string newName)
    {
        // Ordinal throughout (the map's tuple key compares ordinally), so a case-only rename ("Blue" → "blue") is a
        // real move: the drop below never touches the old name's entries.
        var changed = RemoveTheme(newName, stage: false);
        var moved = new List<KeyValuePair<(string Theme, string Slot), ColorRgba>>();
        foreach (var kv in _map) if (string.Equals(kv.Key.Theme, oldName, StringComparison.Ordinal)) moved.Add(kv);
        foreach (var kv in moved) { _map.Remove(kv.Key); _map[(newName, kv.Key.Slot)] = kv.Value; }
        if (moved.Count == 0 && !changed) return false;
        Stage();
        return true;
    }

    public bool RemoveTheme(string themeName) => RemoveTheme(themeName, stage: true);

    public bool CopyTheme(string fromName, string toName)
    {
        if (string.Equals(fromName, toName, StringComparison.Ordinal)) return false;
        var changed = RemoveTheme(toName, stage: false);
        var copied = new List<KeyValuePair<(string Theme, string Slot), ColorRgba>>();
        foreach (var kv in _map) if (string.Equals(kv.Key.Theme, fromName, StringComparison.Ordinal)) copied.Add(kv);
        foreach (var kv in copied) _map[(toName, kv.Key.Slot)] = kv.Value;   // collected first: no add while enumerating
        if (copied.Count == 0 && !changed) return false;
        Stage();
        return true;
    }

    private bool RemoveTheme(string themeName, bool stage)
    {
        var drop = new List<(string Theme, string Slot)>();
        foreach (var k in _map.Keys) if (string.Equals(k.Theme, themeName, StringComparison.Ordinal)) drop.Add(k);
        foreach (var k in drop) _map.Remove(k);
        if (drop.Count > 0 && stage) Stage();
        return drop.Count > 0;
    }

    // Writes the whole map into the config tree WITHOUT saving. Every framework section lives in one config file
    // (PluginConfigService.Save writes the whole tree), so the next Save of ANY section — CustomThemeStore's, on a
    // rename — writes it in the same write as the theme list: the rename lands on disk atomically. Staged = clean.
    private void Stage()
    {
        var raw = new Dictionary<string, string>(_map.Count, StringComparer.Ordinal);
        foreach (var kv in _map) raw[kv.Key.Theme + Sep + kv.Key.Slot] = ToHex(kv.Value);
        _config.Set(MapKey, raw);
        _dirty = false;
    }

    // Splits a persisted "<theme> <slot>" key on the FIRST space. Theme names are
    // validated to contain no spaces and slot keys use dots, so this is unambiguous.
    private static bool TrySplit(string composed, out string theme, out string slot)
    {
        var i = composed.IndexOf(Sep);
        if (i <= 0 || i >= composed.Length - 1) { theme = slot = ""; return false; }
        theme = composed.Substring(0, i);
        slot = composed.Substring(i + 1);
        return true;
    }

    private static string ToHex(ColorRgba c)
    {
        static int B(float f) => Math.Clamp((int)Math.Round(f * 255f), 0, 255);
        return $"#{B(c.R):X2}{B(c.G):X2}{B(c.B):X2}{B(c.A):X2}";
    }

    private static bool TryParseHex(string s, out ColorRgba c)
    {
        c = default;
        if (string.IsNullOrEmpty(s) || s[0] != '#' || s.Length != 9) return false;
        if (!int.TryParse(s.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return false;
        if (!int.TryParse(s.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return false;
        if (!int.TryParse(s.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;
        if (!int.TryParse(s.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)) return false;
        c = new ColorRgba(r / 255f, g / 255f, b / 255f, a / 255f);
        return true;
    }
}
