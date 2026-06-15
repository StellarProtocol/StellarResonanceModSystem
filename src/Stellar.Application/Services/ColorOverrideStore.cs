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
        var raw = new Dictionary<string, string>(_map.Count, StringComparer.Ordinal);
        foreach (var kv in _map) raw[kv.Key.Theme + Sep + kv.Key.Slot] = ToHex(kv.Value);
        _config.Set(MapKey, raw);
        _config.Save();
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
