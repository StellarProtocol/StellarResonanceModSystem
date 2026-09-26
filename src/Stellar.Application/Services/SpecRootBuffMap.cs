using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// The live buff id → spec id map (spec-from-talent-buffs, 2026-09-26). Starts on
/// <see cref="SpecRootBuffs.Fallback"/> so root buffs resolve even before the game tables load;
/// <see cref="LoadFrom"/> (once, game thread, after the deferred game-data drain) swaps in the table-derived
/// map when it has exactly <see cref="SpecRootBuffs.ExpectedCount"/> entries, and otherwise keeps the
/// fallback. Never throws, never blocks. Lookups are lock-free (one volatile reference read) because they
/// run on every buff upsert.
/// </summary>
internal sealed partial class SpecRootBuffMap
{
    private readonly IPluginLog _log;
    private IReadOnlyDictionary<int, int> _map = SpecRootBuffs.Fallback;
    private string _source = "fallback";
    private int _loaded;

    public SpecRootBuffMap(IPluginLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary><c>tables</c> once a table-derived map is active, otherwise <c>fallback</c>.</summary>
    public string Source => Volatile.Read(ref _source);

    /// <summary>True when <paramref name="buffId"/> is a spec root buff; <paramref name="specId"/> is its spec.</summary>
    public bool TryGetSpec(int buffId, out int specId) => Volatile.Read(ref _map).TryGetValue(buffId, out specId);

    /// <summary>Derive the map from the game tables once. Subsequent calls are no-ops.</summary>
    public void LoadFrom(ISpecRootBuffSource source)
    {
        if (Interlocked.Exchange(ref _loaded, 1) != 0) return;
        var derived = TryDerive(source);
        bool fromTables = derived is { Count: SpecRootBuffs.ExpectedCount };
        if (fromTables)
        {
            Volatile.Write(ref _map, derived!);
            Volatile.Write(ref _source, "tables");
        }
        else
        {
            DiagFallback(derived);
        }
        _log.Info($"[CombatSpec] spec root buffs: {Volatile.Read(ref _map).Count} (source={Source})");
    }

    private Dictionary<int, int>? TryDerive(ISpecRootBuffSource source)
    {
        try
        {
            return source.TryReadTalentTables(out var tables) && tables is not null
                ? SpecRootBuffs.Derive(tables)
                : null;
        }
        catch (Exception ex)
        {
            DiagDeriveThrew(ex);
            return null;
        }
    }
}
