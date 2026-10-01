using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Rendering;

/// <summary>StellarDiagnostics-gated logging for the free-camera backend.</summary>
internal sealed partial class CinemachineCameraBackend
{
    partial void OnBegun(CameraPose start)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] vcam on prio={TopPriority} start=({start.Position.X:F2},{start.Position.Y:F2},{start.Position.Z:F2}) yaw={start.Yaw:F1} pitch={start.Pitch:F1} fov={start.Fov:F1}");
    }

    partial void OnEnded()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info("[FreeCam] vcam off — brain blends back to the game camera");
    }
}
