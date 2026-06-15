using System;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void TryLoadGameDataEagerOnce()
    {
        if (_gameDataEagerLoaded || _gameDataProbe is null || _gameDataService is null || _gameDataLog is null)
        {
            return;
        }

        // Tables are loaded asynchronously by Panda.TableInitUtility — at Game.Init
        // postfix the static ZTable<K,V> container exists but Count is still 0.
        // Probe a known-eager table for non-empty before firing the batch; retry
        // each Update tick until ready.
        if (!_gameDataProbe.AreEagerTablesReady())
        {
            return;
        }

        _gameDataEagerLoaded = true;

        _gameDataLog.Info("[Stellar][GameData] eager batch start (6 tables)");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _gameDataService.LoadEager(_gameDataProbe);
            watch.Stop();
            _gameDataLog.Info($"[Stellar][GameData] eager batch complete (total {watch.ElapsedMilliseconds}ms)");
            _gameDataLog.Info($"[Stellar][GameData] IsAvailable={_gameDataService.IsAvailable}");
        }
        catch (Exception ex)
        {
            watch.Stop();
            _gameDataLog.Error($"[Stellar][GameData] eager batch threw after {watch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrainGameDataDeferred()
    {
        if (_gameDataAllLoaded || _gameDataProbe is null || _gameDataService is null || _gameDataLog is null)
        {
            return;
        }
        if (!_gameDataEagerLoaded)
        {
            return;  // eager batch must finish before deferred drain begins
        }

        try
        {
            _gameDataService.DrainDeferred(_gameDataProbe);
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
