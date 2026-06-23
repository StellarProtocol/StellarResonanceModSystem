using System;
using Stellar.Application.Abstractions;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// RECON-ONLY (throwaway) probe. Discovers the game's exchange/marketplace Lua VM and its
/// buy/query functions, dumping findings to the BepInEx log for recon/exchange-vm-notes.md.
/// Constructed only when STELLAR_EXCHANGE_RECON is set (see Wiring.ExchangeRecon.cs).
/// Replaced by the real PandaExchangeProbe in Phase 1; delete this file then.
/// </summary>
internal sealed partial class PandaExchangeReconProbe
{
    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    private int _fireTickCounter;
    private const int FireEveryTicks = 150;   // ~5s at 30 Hz tick — read-only, re-fires for fresh data
    private string? _lastDump;

    public PandaExchangeReconProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    // Called from the Host service tick (main thread — the Lua VM is main-thread-only).
    internal void Tick()
    {
        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;

        if (_fireTickCounter++ % FireEveryTicks == 0)
        {
            InvokeChunk(ReconChunk);
        }

        var dump = ReadLuaGlobalString(ReconGlobal);
        if (dump is null || dump == _lastDump) return;
        _lastDump = dump;
        DumpToLog(dump);
    }

    private void DumpToLog(string dump)
    {
        _log.Info("[Stellar][ExchangeRecon] ===== recon dump begin =====");
        foreach (var line in dump.Split('\n'))
        {
            if (line.Length > 0) _log.Info("[Stellar][ExchangeRecon] " + line);
        }
        _log.Info("[Stellar][ExchangeRecon] ===== recon dump end =====");
    }
}
