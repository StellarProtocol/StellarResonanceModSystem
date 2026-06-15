namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Recon button glue for <see cref="GameUiPanel"/>. Delegates the actual
/// Canvas walk to <see cref="Infrastructure.Game.NativeUiReconWalker"/>
/// so the boot-time auto-recon (gated on STELLAR_NATIVEUI_RECON=1) and the
/// user-triggered click use the same code path.
/// </summary>
internal sealed partial class GameUiPanel
{
    private void ReconWalk()
    {
        _log.Info("[NativeUi/Recon] manual walk via Game UI panel button");
        Stellar.Infrastructure.Game.NativeUiReconWalker.Walk(_log.Info);
        _log.Info("[NativeUi/Recon] manual walk complete");
    }
}
