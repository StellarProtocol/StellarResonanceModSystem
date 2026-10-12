using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in (<c>STELLAR_DIAGNOSTICS=1</c>) dump of parsed AOI buff events, for the
/// in-world verification pass — confirm the 2110xxx imagine lockouts ride the
/// BuffEffectSync path with epoch-ms create times. Lines are prefixed
/// <c>[CooldownBar][diag]</c> so they grep cleanly.
/// </summary>
internal sealed partial class PandaCombatStubProbe
{
    private void DiagBuffEvents(long entityUuid, BuffEventBatch batch)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        DiagBuffTrace(entityUuid, batch.Trace);
        IReadOnlyList<ActiveBuff> upserts = batch.Upserts;
        IReadOnlyList<int> removes = batch.Removes;
        if (upserts.Count == 0 && removes.Count == 0) return;
        _log.Info($"[CooldownBar][diag] buff events entity={entityUuid} local={_localEntityIdValue} +{upserts.Count} -{removes.Count}");
        for (int i = 0; i < upserts.Count; i++)
        {
            var b = upserts[i];
            _log.Info($"[CooldownBar][diag]   +uuid={b.BuffUuid} base={b.BaseId} dur={b.DurationMs} create={b.CreateTimeMs} layer={b.Layer}");
        }
        for (int i = 0; i < removes.Count; i++)
            _log.Info($"[CooldownBar][diag]   -uuid={removes[i]}");
    }

    // Raw per-BuffEffect view (type 2 = Remove, 6 = RemoveLayer; payload 18 = AddBuff, 19 = BuffChange) — what the
    // server actually sent, before the reader decided upsert vs remove.
    private void DiagBuffTrace(long entityUuid, IReadOnlyList<BuffEffectTrace>? trace)
    {
        if (trace is null) return;
        for (int i = 0; i < trace.Count; i++)
        {
            var t = trace[i];
            _log.Info($"[CooldownBar][diag]   raw entity={entityUuid} type={t.Type} uuid={t.BuffUuid} payload={t.PayloadKind} " +
                      $"payloadUuid={t.PayloadUuid} layer={t.Layer} dur={t.DurationMs} create={t.CreateTimeMs}");
        }
    }
}
