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
    // Bumped by RegisterOwned/Unregister/Relabel — lets a consumer that caches Slots by SlotCount (stable
    // across a Relabel) also detect a label-only change and refresh. See IThemeOverrides.Revision.
    private int _revision;

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
        _revision++;
        return new RegisteredColorSlot(this, this, key);
    }

    public void Unregister(string key)
    {
        if (_slots.Remove(key)) { _order.Remove(key); _revision++; }
    }

    /// <summary>Framework-only: re-sets an already-registered slot's display LABEL (never its key, owner or
    /// colour defaults) — e.g. once the real UI language is known, or on a later language switch. Not part of
    /// the plugin-facing <see cref="IColorRegistry"/> surface; a no-op if <paramref name="key"/> isn't registered.</summary>
    internal void Relabel(string key, string label)
    {
        if (!_slots.TryGetValue(key, out var d) || d.Label == label) return;   // no-op: unknown key or unchanged
        _slots[key] = d with { Label = label };
        _revision++;
    }

    /// <summary>Bumped on every Register/Unregister/Relabel — a slot-list cache keyed on <see cref="SlotCount"/>
    /// alone misses a Relabel (the count doesn't change), so key the cache on this too.</summary>
    public int Revision => _revision;

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
