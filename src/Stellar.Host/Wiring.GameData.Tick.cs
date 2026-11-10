using System;
using System.Diagnostics;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // Per-tick wall-clock budget for game-data table loading (eager batch, then the deferred queue). Raid log
    // 2026-11-10: unsliced, fw:gamedata hit 275 ms (whole eager batch in one frame) and 252 / 180 / 120 ms for
    // single deferred tables. The loads now stop at this deadline and resume next tick (one table may span many
    // ticks; each still lands in its cache in one piece). ~5 ms of a 30 Hz beat keeps a frame smooth; the
    // whole ~0.9 s of login table work then completes over a few seconds instead of in a handful of frames.
    private const double GameDataSliceBudgetMs = 5.0;

    private bool _gameDataEagerStarted;
    private Stopwatch? _gameDataEagerWatch;

    // The fw:gamedata job: eager batch → deferred queue → spec-root talent tables, sharing ONE deadline so a
    // stage that finishes mid-tick hands only the remaining budget to the next.
    private void TickGameData()
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(GameDataSliceBudgetMs * Stopwatch.Frequency / 1000.0);
        TryLoadGameDataEagerOnce(deadline);   // until the eager batch is done (once Bokura.*TableBase handles are populated)
        DrainGameDataDeferred(deadline);      // then the deferred queue, sliced; no-op until eager done / queue empty
        StepSpecRootBuffs();                  // then the 3 talent tables for the spec-root map, one per tick
    }

    [WorldGated]
    private void TryLoadGameDataEagerOnce(long deadline)
    {
        if (!_clientState!.IsWorldActive) return;   // reads live game tables — never during the connect handshake
        if (_gameDataEagerLoaded || _gameDataProbe is null || _gameDataService is null || _gameDataLog is null)
        {
            return;
        }

        if (!_gameDataEagerStarted)
        {
            // Tables are loaded asynchronously by Panda.TableInitUtility — at Game.Init
            // postfix the static ZTable<K,V> container exists but Count is still 0.
            // Probe a known-eager table for non-empty before starting the batch; retry
            // each Update tick until ready.
            if (!_gameDataProbe.AreEagerTablesReady())
            {
                return;
            }
            _gameDataEagerStarted = true;
            _gameDataLog.Info("[Stellar][GameData] eager batch start (6 tables)");
            _gameDataEagerWatch = Stopwatch.StartNew();
        }

        try
        {
            if (!_gameDataService.StepEager(_gameDataProbe, deadline)) return;   // more slices next tick
            _gameDataEagerLoaded = true;
            _gameDataLog.Info($"[Stellar][GameData] eager batch complete (wall {_gameDataEagerWatch!.ElapsedMilliseconds}ms, sliced)");
            _gameDataLog.Info($"[Stellar][GameData] IsAvailable={_gameDataService.IsAvailable}");
        }
        catch (Exception ex)
        {
            _gameDataEagerLoaded = true;   // one attempt, as before — never retry a throwing batch every tick
            _gameDataLog.Error($"[Stellar][GameData] eager batch threw after {_gameDataEagerWatch!.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [WorldGated]
    private void DrainGameDataDeferred(long deadline)
    {
        if (!_clientState!.IsWorldActive) return;   // reads live game tables — never during the connect handshake
        if (_gameDataAllLoaded || _gameDataProbe is null || _gameDataService is null || _gameDataLog is null)
        {
            return;
        }
        if (!_gameDataEagerLoaded)
        {
            return;  // eager batch must finish before deferred drain begins
        }
        if (Stopwatch.GetTimestamp() >= deadline) return;   // the eager batch used this tick's budget

        try
        {
            _gameDataService.DrainDeferred(_gameDataProbe, deadline);
        }
        catch (Exception ex)
        {
            _gameDataLog.Error($"[Stellar][GameData] deferred drain threw: {ex.GetType().Name}: {ex.Message}");
        }

        // Completion comes from the service's own queue (a hardcoded count here once skipped the
        // 3 equip tables added in Phase 2 — caught in-world 2026-06-12).
        if (_gameDataService.DeferredComplete)
        {
            _gameDataAllLoaded = true;
            _gameDataLog.Info("[Stellar][GameData] all tables loaded");
        }
    }
}
