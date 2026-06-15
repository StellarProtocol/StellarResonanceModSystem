// src/Stellar.Application/Services/ColorRegistryService.cs
using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Owns colour-slot descriptors + resolution. Implements the plugin
/// registration surface and the read-side resolver (editor surfaces added in
/// Task 4). Single source of truth so Settings stays plugin-agnostic.</summary>
internal sealed class ColorRegistryService : IColorRegistry, IColorResolver, IThemeOverrides
{
    public static readonly ColorRgba MissingSentinel = new(1f, 0f, 1f, 1f); // magenta

    private sealed record Descriptor(string Key, string Owner, string Label,
        IReadOnlyDictionary<ThemePreset, ColorRgba> Defaults);

    private readonly Dictionary<string, Descriptor> _slots = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly INamedTheme _namedTheme;
    private readonly IColorOverrideStore _overrides;

    public ColorRegistryService(INamedTheme namedTheme, IColorOverrideStore overrides)
    {
        _namedTheme = namedTheme;
        _overrides = overrides;
    }

    public IColorSlot Register(string key, string label,
        IReadOnlyDictionary<ThemePreset, ColorRgba> defaults)
        => RegisterOwned(OwnerOf(key), key, label, defaults);

    public IColorSlot Register(string key, string label, ColorRgba defaultAll)
    {
        var allPresets = new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = defaultAll,
            [ThemePreset.Dark]    = defaultAll,
            [ThemePreset.Light]   = defaultAll,
            [ThemePreset.Crimson] = defaultAll,
        };
        return Register(key, label, allPresets);
    }

    internal IColorSlot RegisterOwned(string owner, string key, string label,
        IReadOnlyDictionary<ThemePreset, ColorRgba> defaults)
    {
        if (_slots.ContainsKey(key))
            throw new ArgumentException($"colour slot already registered: {key}", nameof(key));
        _slots[key] = new Descriptor(key, owner, label, new Dictionary<ThemePreset, ColorRgba>(defaults));
        _order.Add(key);
        return new RegisteredColorSlot(this, this, key);
    }

    public void Unregister(string key)
    {
        if (_slots.Remove(key)) _order.Remove(key);
    }

    public ColorRgba Resolve(string slotKey)
    {
        if (!_slots.TryGetValue(slotKey, out var d)) return MissingSentinel;
        if (_namedTheme.ActiveCustomName is { } themeName
            && _overrides.TryGet(themeName, slotKey, out var ovr)) return ovr;
        var basePreset = _namedTheme.Active;
        if (d.Defaults.TryGetValue(basePreset, out var def)) return def;
        return d.Defaults.TryGetValue(ThemePreset.Default, out var fb) ? fb : MissingSentinel;
    }

    public int SlotCount => _order.Count;

    public IReadOnlyList<ColorSlotInfo> Slots
    {
        get
        {
            var list = new List<ColorSlotInfo>(_order.Count);
            foreach (var key in _order)
            {
                var d = _slots[key];
                list.Add(new ColorSlotInfo(d.Key, d.Owner, d.Label));
            }
            return list;
        }
    }

    public bool HasOverride(string slotKey)
        => _namedTheme.ActiveCustomName is { } t && _overrides.Has(t, slotKey);

    public void SetOverride(string slotKey, ColorRgba value)
    {
        if (_namedTheme.ActiveCustomName is not { } t) return; // built-ins read-only
        _overrides.Set(t, slotKey, value);
    }

    public void ClearOverride(string slotKey)
    {
        if (_namedTheme.ActiveCustomName is { } t) _overrides.Clear(t, slotKey);
    }

    public void Flush() => _overrides.Flush();

    private static string OwnerOf(string key)
    {
        var dot = key.IndexOf('.');
        return dot > 0 ? key.Substring(0, dot) : key;
    }
}
