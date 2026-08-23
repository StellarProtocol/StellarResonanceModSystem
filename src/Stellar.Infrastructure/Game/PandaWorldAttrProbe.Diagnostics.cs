namespace Stellar.Infrastructure.Game;

// Diagnostics for PandaWorldAttrProbe — gated on StellarDiagnostics.IsEnabled per the standards.
// One line per genuinely-changed Defeated count, naming WHICH scene-attr carrier delivered it
// (the zone-in seed vs a mid-run SyncSceneAttrs), so an owner run can tell "the seed was the only
// thing that ever fired" from "the event stream is live" without any guessing.
internal sealed partial class PandaWorldAttrProbe
{
    private void DiagDefeated(int value, string source)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Defeated] AttrDeathCount(348) = {value} via {source} — latched (runId={_state.CurrentRunId})");
    }
}
