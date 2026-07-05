using System.Collections.Generic;
using System.Text;

namespace Stellar.Infrastructure.Game;

// Read-only WorldNtf methodId census (Phase 1 of the dungeon-clock capture).
// Gated on StellarDiagnostics.IsEnabled; logs each distinct WorldNtf methodId
// once (with a short payload hex prefix) so the dungeon dirty-delta's true id
// and blob shape can be identified + validated offline before any capture code.
internal sealed partial class WorldNtfStubDispatcher
{
    private readonly HashSet<uint> _censusSeen = new();
    // Dirty-delta methods (container-merge blobs). We log the FULL hex for these
    // and allow several occurrences — the timer_info-bearing dungeon delta (24) is
    // not necessarily the first — so the real blob can validate DungeonDirtyDataReader
    // offline. 22 = SyncContainerDirtyData (inventory precedent), 24 = SyncDungeonDirtyData.
    private static readonly HashSet<uint> CensusFullDump = new() { 22, 24 };
    private readonly Dictionary<uint, int> _censusFullSeen = new();
    private const int CensusFullMaxPerMethod = 6;

    private void DiagCensus(uint methodId, object stubCall)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;

        if (CensusFullDump.Contains(methodId))
        {
            var seen = _censusFullSeen.TryGetValue(methodId, out var n) ? n : 0;
            if (seen >= CensusFullMaxPerMethod) return;
            _censusFullSeen[methodId] = seen + 1;
            var full = ExtractPayload(stubCall);
            _log.Info($"[Census] WorldNtf method={methodId} #{seen} len={full?.Length ?? -1} FULLHEX={Hex(full, int.MaxValue)}");
            return;
        }

        if (!_censusSeen.Add(methodId)) return;   // once per distinct id — bounds volume
        var bytes = ExtractPayload(stubCall);
        _log.Info($"[Census] WorldNtf method={methodId} len={bytes?.Length ?? -1} hex={Hex(bytes, 24)}");
    }

    private static string Hex(byte[]? bytes, int max)
    {
        if (bytes is null || bytes.Length == 0) return "(none)";
        var take = bytes.Length < max ? bytes.Length : max;
        var sb = new StringBuilder(take * 2);
        for (var i = 0; i < take; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
