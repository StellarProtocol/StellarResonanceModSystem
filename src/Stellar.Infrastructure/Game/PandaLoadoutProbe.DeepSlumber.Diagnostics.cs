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
/// the first read that carries a "DSLV" row at all — i.e. once the refresh chunk's DS section has
/// actually RUN — carrying whatever it found: meaningful line/area/node/season-level counts, the
/// "DSN" cultivate-line walk count, and any "DSERR" section failures (Task: DS iteration fix, owner
/// run sea/O1jJepsgKC, 2026-08-20 — production evidence was an entirely empty, error-silent capture,
/// so this now surfaces emptiness and errors instead of staying quiet about them). A read with no
/// "DSLV" row at all (bridge unresolved, or a stale in-flight read from before this enrichment
/// shipped) logs nothing and leaves the latch unset — non-latching on that case only — so the first
/// read that actually ran the walk is the one that gets logged, even if it takes a few ticks after
/// the bridge resolves.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private const int DeepSlumberSeasonLevelLogCap = 8;

    private bool _deepSlumberFirstReadLogged;

    private void LogDeepSlumberFirstRead(DeepSlumberState? state, int? dsLineCount, IReadOnlyList<string> dsErrors)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_deepSlumberFirstReadLogged) return;
        if (state is null) return;   // no "DSLV" row at all — the DS walk hasn't run yet
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
        string lineCountText = dsLineCount.HasValue ? dsLineCount.Value.ToString() : "n/a";
        string errorsText = dsErrors.Count == 0 ? "none" : string.Join(" | ", dsErrors);

        _log.Info(
            $"[Stellar][Loadout][DeepSlumber] first read via Lua bridge: {distinctLines.Count} line(s), "
            + $"{state.Lines.Count} line/subType variant(s), {areaCount} area(s); "
            + $"nodes big={bigNodes} middle={middleNodes} normal={normalNodes}; "
            + $"{state.SeasonLevels.Count} season level(s) seasonLevels=[{seasonLevels}]; "
            + $"cultivateLinesWalked={lineCountText}; errors=[{errorsText}]");
    }
}
