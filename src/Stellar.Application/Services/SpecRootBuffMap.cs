using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// The live buff id → spec id map (spec-from-talent-buffs, 2026-09-26). Starts on
/// <see cref="SpecRootBuffs.Fallback"/> so root buffs resolve even before the game tables load. Once the
/// deferred game-data drain is done, Host calls <see cref="LoadStep"/> once per game-thread tick: one table per
/// step (stages → the 18 root tree nodes → their talents' effects), so the reads are spread like every other
/// deferred table. The table-derived map is adopted only with exactly <see cref="SpecRootBuffs.ExpectedCount"/>
/// entries, otherwise the fallback stays. Never throws, never blocks. Lookups are lock-free (one volatile
/// reference read) because they run on every buff upsert.
/// </summary>
internal sealed partial class SpecRootBuffMap
{
    private readonly IPluginLog _log;
    private IReadOnlyDictionary<int, int> _map = SpecRootBuffs.Fallback;
    private string _source = "fallback";

    // Staged load state — game thread only.
    private int _step;                                       // 0 stages, 1 trees, 2 effects, 3 done
    private IReadOnlyDictionary<int, TalentStageRow>? _stages;
    private IReadOnlyDictionary<int, int>? _trees;

    public SpecRootBuffMap(IPluginLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary><c>tables</c> once a table-derived map is active, otherwise <c>fallback</c>.</summary>
    public string Source => Volatile.Read(ref _source);

    /// <summary>True when <paramref name="buffId"/> is a spec root buff; <paramref name="specId"/> is its spec.</summary>
    public bool TryGetSpec(int buffId, out int specId) => Volatile.Read(ref _map).TryGetValue(buffId, out specId);

    /// <summary>Advance the staged table load by one table. Returns true once loading has finished (further
    /// calls are no-ops that return true).</summary>
    public bool LoadStep(ISpecRootBuffSource source)
    {
        if (_step >= 3) return true;
        try
        {
            switch (_step)
            {
                case 0: _stages = source.ReadTalentStages(); break;
                case 1: _trees = source.ReadTalentTreeTalentIds(SpecRootBuffs.RootTreeIds(_stages!)); break;
                default: Finish(source.ReadTalentEffects(new HashSet<int>(_trees!.Values))); return true;
            }
            _step++;
            return false;
        }
        catch (Exception ex)
        {
            DiagDeriveThrew(ex);
            Finish(null);
            return true;
        }
    }

    private void Finish(IReadOnlyDictionary<int, TalentEffectRow>? talents)
    {
        _step = 3;
        Dictionary<int, int>? derived = null;
        if (talents is { Count: > 0 } && _stages is { Count: > 0 } && _trees is { Count: > 0 })
            derived = SpecRootBuffs.Derive(new SpecTalentTables(_stages, _trees, talents));
        if (derived is { Count: SpecRootBuffs.ExpectedCount })
        {
            Volatile.Write(ref _map, derived);
            Volatile.Write(ref _source, "tables");
        }
        else
        {
            DiagFallback(derived);
        }
        _stages = null;
        _trees = null;
        _log.Info($"[CombatSpec] spec root buffs: {Volatile.Read(ref _map).Count} (source={Source})");
    }
}
