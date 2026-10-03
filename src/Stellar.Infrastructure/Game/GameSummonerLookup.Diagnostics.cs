using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameSummonerLookup
{
    private const int MaxLoggedCasters = 100;
    private readonly HashSet<long> _loggedCasters = new();   // distinct casters logged this session, capped

    /// <summary>One line per distinct caster (first lookup only, capped at <see cref="MaxLoggedCasters"/>) so the owner's
    /// next run shows what the game answered for each unannounced summon: its marker, both raw summoner reads, and the
    /// owner handed back with that owner's own marker kind (the classifier then maps it to a layer).</summary>
    partial void OnLookedUp(long uuid, long top, long summoner, long owner)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_loggedCasters.Count >= MaxLoggedCasters || !_loggedCasters.Add(uuid)) return;
        _log.Info($"[EffectHide] owner-lookup caster={uuid} marker={uuid & 0xFFFF} top={top} summoner={summoner} " +
                  $"-> owner={owner}({Kind(owner)})");
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
