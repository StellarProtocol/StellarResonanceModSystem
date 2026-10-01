using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the input shield.</summary>
internal sealed partial class ZIgnoreShieldBackend
{
    partial void OnShieldSet(bool on)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] input shield {(on ? "on" : "off")} mask=0x{_mask:X} source={SourceName}");
    }
}
