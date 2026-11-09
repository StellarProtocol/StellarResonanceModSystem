namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// The HUD-structure dump ("Save HUD paths", Settings → About → Diagnostics; was "Recon paths…" on this tab).
/// Delegates the actual Canvas walk to <see cref="Infrastructure.Game.NativeUiReconWalker"/> so the boot-time
/// auto-recon (gated on STELLAR_NATIVEUI_RECON=1) and the user-triggered click use the same code path.
/// </summary>
internal sealed partial class GameUiPanel
{
    internal void ReconWalk()
    {
        _log.Info("[NativeUi/Recon] manual walk via Settings > About > Save HUD paths");
        Stellar.Infrastructure.Game.NativeUiReconWalker.Walk(_log.Info);
        _log.Info("[NativeUi/Recon] manual walk complete");
    }
}
