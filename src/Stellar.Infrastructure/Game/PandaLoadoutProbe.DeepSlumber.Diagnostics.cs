using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for the Lua-bridge Deep-Slumber read
/// (<see cref="PandaLoadoutProbe.UpdateDeepSlumberState"/>). Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>, mirroring
/// <c>PandaInventoryPullReader.DeepSlumber.Diagnostics.cs</c>'s own one-shot pattern. Fires once on
/// the first MEANINGFUL read (at least one cultivate line or season level present) so an in-game
/// verify pass can confirm the Lua-bridge shape (line/subType/area counts, node counts per kind,
/// season levels) against the owner's reference screenshots. A null/entirely-empty read logs nothing
/// and leaves the latch unset — non-latching on empty — so the first read that actually carries data
/// is the one that gets logged, even if it takes a few ticks after the bridge resolves.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private const int DeepSlumberSeasonLevelLogCap = 8;

    private bool _deepSlumberFirstReadLogged;

    private void LogDeepSlumberFirstRead(DeepSlumberState? state)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_deepSlumberFirstReadLogged) return;
        if (state is null || (state.Lines.Count == 0 && state.SeasonLevels.Count == 0)) return;
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

        string seasonLevels = string.Join(",",
            state.SeasonLevels.Take(DeepSlumberSeasonLevelLogCap).Select(pair => $"{pair[0]}:{pair[1]}"));

        _log.Info(
            $"[Stellar][Loadout][DeepSlumber] first read via Lua bridge: {distinctLines.Count} line(s), "
            + $"{state.Lines.Count} line/subType variant(s), {areaCount} area(s); "
            + $"nodes big={bigNodes} middle={middleNodes} normal={normalNodes}; "
            + $"{state.SeasonLevels.Count} season level(s) seasonLevels=[{seasonLevels}]");
    }
}
