using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for <see cref="PandaFashionProbe"/>. Per-event capture/dispatch/result
/// lines are gated on <see cref="StellarDiagnostics.IsEnabled"/>; the one-shot bridge-resolution line
/// fires unconditionally so a non-diagnostic run still shows the Lua bridge resolved.
/// </summary>
internal sealed partial class PandaFashionProbe
{
    private int _failedResolutionAttempts;
    private const int ResolutionFailureLogEvery = 60;

    private void OnResolutionSucceeded()
        => _log.Info("[Stellar][Wardrobe] Lua bridge resolved");

    // Throttled — a pre-login tick storm would otherwise spam identical "not loaded yet" lines.
    private void OnResolutionFailure(string reason)
    {
        if (_resolutionFailureLogged && _failedResolutionAttempts++ % ResolutionFailureLogEvery != 0) return;
        _resolutionFailureLogged = true;
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Wardrobe] bridge unresolved: {reason}");
    }

    private void DiagCaptured(IReadOnlyDictionary<int, int> worn)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[WardrobeCapture] worn {FormatOutfit(worn)}");
    }

    private void DiagWeaponCaptured(WardrobeWeaponSkin skin)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[WardrobeCapture] weapon skin class={skin.ProfessionId} skin={skin.SkinId}");
    }

    // `label` BUILDS the description — FormatOutfit(...) for an outfit apply, "weapon skin class=… skin=…"
    // for a weapon-skin apply. It is invoked only AFTER the gate, so a diagnostics-off apply never formats
    // the outfit map (which allocates a string per region).
    private void DiagDispatched(Func<string> label)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[WardrobeApply] dispatch {label()}");
    }

    private void DiagResult(int code, long elapsedMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[WardrobeApply] result code={code} in {elapsedMs}ms");
    }

    private static string FormatOutfit(IReadOnlyDictionary<int, int> outfit)
    {
        var sb = new StringBuilder();
        var worn = 0;
        foreach (var region in WardrobeRegions.All)
        {
            outfit.TryGetValue(region, out var id);
            if (id == 0) continue;
            worn++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(region).Append(':').Append(id);
        }
        return $"[{worn} worn] {sb}";
    }
}
