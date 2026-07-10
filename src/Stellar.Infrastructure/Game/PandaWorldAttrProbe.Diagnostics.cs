namespace Stellar.Infrastructure.Game;

// Diagnostics for PandaWorldAttrProbe — gated on StellarDiagnostics.IsEnabled per the standards.
// These confirm the ZWorld AttrDeathCount(348) read path in-game (the accessor is a lua-bridge
// method not visible in static tooling, so the first live run validates it).
internal sealed partial class PandaWorldAttrProbe
{
    private bool _resolveMissingLogged;

    private void DiagDefeated(int value)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Defeated] ZWorld AttrDeathCount(348) = {value} — latched (runId={_state.CurrentRunId})");
    }

    private void DiagResolveMissing(string what)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        if (_resolveMissingLogged) return;
        _resolveMissingLogged = true;
        _log.Warning($"[Defeated] ZWorld read disabled — could not resolve {what}");
    }

    private void DiagFaulted(System.Exception ex)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        _log.Warning($"[Defeated] ZWorld read disabled after fault: {ex.GetType().Name}: {ex.Message}");
    }

    private bool _attrShapeLogged;

    // One-shot dump of the returned IAttr wrapper's member surface, so the value accessor can be
    // pinpointed when neither "Value" nor an int-typed property resolves. Reflection-only (no game
    // calls), gated + logged once.
    private void DiagAttrShape(object attr)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        if (_attrShapeLogged) return;
        _attrShapeLogged = true;
        var t = attr.GetType();
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
        var props = string.Join(",", System.Array.ConvertAll(t.GetProperties(F), p => $"{p.Name}:{p.PropertyType.Name}"));
        var fields = string.Join(",", System.Array.ConvertAll(t.GetFields(F), f => $"{f.Name}:{f.FieldType.Name}"));
        _log.Warning($"[Defeated] IAttr value accessor not found — type={t.FullName} props=[{props}] fields=[{fields}]");
    }
}
