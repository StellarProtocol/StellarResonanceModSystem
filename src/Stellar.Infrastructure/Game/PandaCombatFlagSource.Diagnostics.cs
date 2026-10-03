using Stellar.Abstractions.Diagnostics;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the combat-flag hooks (the owner's "in combat" checklist item is the proof).</summary>
internal sealed partial class PandaCombatFlagSource
{
    partial void OnFlag(CombatFlagKind kind, bool on)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] combat flag {kind}={on}");
    }
}
