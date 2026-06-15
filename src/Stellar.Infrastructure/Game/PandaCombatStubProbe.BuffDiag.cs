using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in (<c>STELLAR_DIAGNOSTICS=1</c>) dump of parsed AOI buff events, for the
/// in-world verification pass — confirm the 2110xxx imagine lockouts ride the
/// BuffEffectSync path with epoch-ms create times. Lines are prefixed
/// <c>[CooldownBar][diag]</c> so they grep cleanly.
/// </summary>
internal sealed partial class PandaCombatStubProbe
{
    private void DiagBuffEvents(long entityUuid, IReadOnlyList<ActiveBuff> upserts, IReadOnlyList<int> removes)
    {
        if (!StellarDiagnostics.IsEnabled) return;
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
}
