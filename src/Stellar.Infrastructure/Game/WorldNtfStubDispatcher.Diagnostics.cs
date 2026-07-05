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

    private void DiagCensus(uint methodId, object stubCall)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        if (!_censusSeen.Add(methodId)) return;   // once per distinct id — bounds volume

        var bytes = ExtractPayload(stubCall);
        var len = bytes?.Length ?? -1;
        _log.Info($"[Census] WorldNtf method={methodId} len={len} hex={HexPrefix(bytes)}");
    }

    private static string HexPrefix(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return "(none)";
        var take = bytes.Length < 24 ? bytes.Length : 24;
        var sb = new StringBuilder(take * 2);
        for (var i = 0; i < take; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
