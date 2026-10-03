using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameSummonerLookup
{
    private const int MaxLoggedCasters = 100;
    private readonly HashSet<long> _loggedCasters = new();   // distinct casters logged this session, capped
    private readonly HashSet<long> _loggedHits = new();      // casters whose first non-zero answer was logged

    /// <summary>Per distinct caster (capped at <see cref="MaxLoggedCasters"/> casters): the first fresh read, and — when
    /// that was a miss — also the first later HIT, so a summoner that arrives late shows up in the log. Prints the
    /// caster's marker, both raw summoner reads, and the owner handed back with its own marker kind (the classifier
    /// then maps it to a layer). Memo hits are never re-read, so each line is a real game read.</summary>
    partial void OnLookedUp(long uuid, SummonerRead read, long owner)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var first = !_loggedCasters.Contains(uuid);
        if (first && _loggedCasters.Count >= MaxLoggedCasters) return;
        if (!first && (owner == 0 || _loggedHits.Contains(uuid))) return;
        _loggedCasters.Add(uuid);
        if (owner != 0) _loggedHits.Add(uuid);
        var outcome = owner == 0 ? "miss" : first ? "hit" : "late-hit";
        _log.Info($"[EffectHide] owner-lookup caster={uuid} marker={uuid & 0xFFFF} top={read.Top} summoner={read.Summoner} " +
                  $"-> owner={owner}({Kind(owner)}) {outcome}");
    }

    private static string Kind(long uuid)
    {
        if (uuid == 0) return "none";
        var low = uuid & 0xFFFF;
        if (low == 640) return "player";
        if (low == 64 || low == 32832) return "monster";
        return $"other(low={low})";
    }
}
