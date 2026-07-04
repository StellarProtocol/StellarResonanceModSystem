using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaDungeonSyncServiceHook"/>. One-shot lines
/// are ALWAYS-ON (they confirm the hook fires live — the key uncertainty of a
/// freshly traced seam); per-event repeats gate on
/// <c>STELLAR_DIAGNOSTICS=1</c>. Both run on the MessagePipe publish thread —
/// bounded to one line each in steady state so the capture path stays inert.
/// </summary>
internal sealed partial class PandaDungeonSyncServiceHook
{
    private bool _firstCaptureLogged;
    private bool _extractFailWarned;

    // One-shot always-on: the first captured delta proves the
    // DungeonSyncService seam is live end-to-end (patch fired + bytes
    // extracted). Repeats only under the diagnostics toggle.
    private void DiagDeltaCaptured(int byteCount)
    {
        if (!_firstCaptureLogged)
        {
            _firstCaptureLogged = true;
            _log.Info($"[DungeonSyncHook] first container delta captured ({byteCount} bytes) — DungeonSyncService seam live; parsing at drain");
            return;
        }
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[DungeonSyncHook] delta captured ({byteCount} bytes)");
    }

    // One-shot always-on: the prefix fired but the event → BufferStream →
    // ByteString walk failed — the interop surface differs from the recon'd
    // shape and the extraction needs a revisit.
    private void DiagExtractFailed()
    {
        if (_extractFailWarned) return;
        _extractFailWarned = true;
        _log.Warning("[DungeonSyncHook] prefix fired but delta bytes could not be extracted (VData/Buffer/ToByteArray walk failed) — interop shape mismatch");
    }
}
