using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaDungeonProbe"/>, gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state observer path pays zero log
/// cost. The first detected dungeon-sync is ALWAYS logged once (regardless of
/// the toggle) because it surfaces the WorldNtf <b>method id</b> the packet
/// arrived on — the one datum we can't get offline — so the user can later
/// switch this structural detection to a direct, cheaper method-id registration.
/// Subsequent detections only log when diagnostics is on.
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    private bool _firstDetectionLogged;

    private void DiagDungeonSync(uint methodId, DungeonSyncResult result)
    {
        // One-shot, always-on: capture the method id so the user can confirm /
        // optimize to a direct WorldNtfMethodIds.SyncDungeonData registration.
        if (!_firstDetectionLogged)
        {
            _firstDetectionLogged = true;
            _log.Info(
                $"[Dungeon] first SyncDungeonData detected on WorldNtf method={methodId} " +
                $"scene_uuid={result.SceneUuid} settlement={result.HasSettlement} " +
                $"pass_time={result.PassTimeSeconds}s mastermode_score={result.MasterModeScore} " +
                "(structural match — register this method id directly to skip the catch-all scan)");
            return;
        }

        if (!StellarDiagnostics.IsEnabled) return;

        _log.Info(
            $"[Dungeon] SyncDungeonData method={methodId} scene_uuid={result.SceneUuid} " +
            $"settlement={result.HasSettlement} pass_time={result.PassTimeSeconds}s " +
            $"mastermode_score={result.MasterModeScore}");
    }
}
