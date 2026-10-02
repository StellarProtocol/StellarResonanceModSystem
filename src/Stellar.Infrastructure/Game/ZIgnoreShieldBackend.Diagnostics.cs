using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the input shield.</summary>
internal sealed partial class ZIgnoreShieldBackend
{
    partial void OnShieldSet(bool camera, bool pause, ulong cleared, ulong added)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] input shield camera={(camera ? "on" : "off")} pause={(pause ? "on" : "off")} " +
                  $"applied=0x{_applied:X} cleared=0x{cleared:X} added=0x{added:X} source={SourceName}");
    }
}
