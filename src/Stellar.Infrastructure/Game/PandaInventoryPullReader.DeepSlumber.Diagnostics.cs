using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for the Deep-Slumber (season cultivate) reflection walk
/// (<see cref="PandaInventoryPullReader.ReadDeepSlumber"/>). Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>, mirroring the inventory reader's own
/// <c>.Diagnostics.cs</c> partial. Fires once on the first successful read so an in-game
/// verify pass can confirm the walked shape (line/subType/area counts, node counts per
/// kind, season levels) against the owner's reference screenshots.
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private bool _deepSlumberFirstReadLogged;

    private void OnDeepSlumberReadLogged(DeepSlumberState state)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_deepSlumberFirstReadLogged) return;
        _deepSlumberFirstReadLogged = true;

        var distinctLines = new HashSet<int>();
        int areaCount = 0, bigNodes = 0, middleNodes = 0, normalNodes = 0;
        foreach (var line in state.Lines)
        {
            distinctLines.Add(line.LineId);
            areaCount += line.Areas.Count;
            foreach (var area in line.Areas)
            {
                bigNodes += area.BigNodes.Count;
                middleNodes += area.MiddleNodes.Count;
                normalNodes += area.NormalNodes.Count;
            }
        }

        _log.Info(
            $"[Stellar][DeepSlumber] first read: {distinctLines.Count} line(s), "
            + $"{state.Lines.Count} line/subType variant(s), {areaCount} area(s); "
            + $"nodes big={bigNodes} middle={middleNodes} normal={normalNodes}; "
            + $"{state.SeasonLevels.Count} season level(s)");
    }
}
