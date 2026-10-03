using System;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Application.Services;

/// <summary>Lights diagnostics (STELLAR_DIAGNOSTICS=1): lamp count on every add / remove, the gate's saved value + flags
/// on raise and what it restored, and every key / rim write and write-back per person.</summary>
internal sealed partial class LightsService
{
    private readonly Action<string> _info;

    partial void OnLampsChanged(string what, int id)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _info($"[Lights] lamp {what} id={id} lamps={_lamps.Count}");
    }

    partial void OnGateRaised(LightGate gate)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var s = gate.Saved;
        _info($"[Lights] gate raised level={gate.Level:F2} saved value={s.Value:F3} active={s.Active} overriding={s.Overriding}/{s.Flags}");
    }

    partial void OnGateRestored(LightGate gate)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var s = gate.Saved;
        _info($"[Lights] gate restored value={s.Value:F3} active={s.Active} overriding={s.Overriding}/{s.Flags}");
    }

    partial void OnPersonWritten(long uuid, string what, int saved)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _info($"[Lights] person {uuid} {what} written (snapshot {saved} values)");
    }

    partial void OnPersonRestored(long uuid, string what, int writes)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _info($"[Lights] person {uuid} {what} restored ({writes} write-backs)");
    }

    partial void OnReleasedAll()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _info($"[Lights] scene end: released (lamps={_lamps.Count} people={_people.Count} gate={(_gate is null ? "restored" : "RAISED")})");
    }
}
