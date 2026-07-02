using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaDungeonProbe"/>, gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state dispatch path pays zero log
/// cost. The first detected dungeon-sync is ALWAYS logged once (regardless of
/// the toggle) as a boot sanity check confirming the direct method-id
/// registration (23) is receiving and structurally matching packets.
/// Subsequent detections only log when diagnostics is on.
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    private bool _firstDetectionLogged;

    private void DiagDungeonSync(uint methodId, DungeonSyncResult result)
    {
        // One-shot, always-on: confirm the registered method id is delivering
        // structurally-valid SyncDungeonData payloads.
        if (!_firstDetectionLogged)
        {
            _firstDetectionLogged = true;
            _log.Info(
                $"[Dungeon] first SyncDungeonData detected on WorldNtf method={methodId} " +
                $"scene_uuid={result.SceneUuid} settlement={result.HasSettlement} " +
                $"pass_time={result.PassTimeSeconds}s mastermode_score={result.MasterModeScore}");
            return;
        }

        if (!StellarDiagnostics.IsEnabled) return;

        _log.Info(
            $"[Dungeon] SyncDungeonData method={methodId} scene_uuid={result.SceneUuid} " +
            $"settlement={result.HasSettlement} pass_time={result.PassTimeSeconds}s " +
            $"mastermode_score={result.MasterModeScore}");
    }
}
