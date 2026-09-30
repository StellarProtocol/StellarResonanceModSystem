namespace Stellar.Application.Abstractions;

/// <summary>
/// Outcome of probing whether one <c>VisibilityLayers</c> layer's game-side reflection target resolves.
/// Pure decision consumed by <c>GameVisibilityBackend</c>'s per-layer probes (see
/// <c>GameVisibilityBackend.Reflection.cs</c>) — it never touches reflection itself, only the two facts a probe
/// already computed.
/// </summary>
internal enum ProbeOutcome
{
    /// <summary>The hot-update type hasn't loaded yet (hot-update not ready) — report the layer available
    /// (optimistic); there is nothing to cache, since the next probe must look again.</summary>
    Optimistic,

    /// <summary>The type loaded and the expected member resolved — report available. The caller caches the
    /// resolved member itself (that cache already exists for the setters to reuse); this decision needs no
    /// cache of its own.</summary>
    Available,

    /// <summary>The type loaded and the expected member is genuinely missing, OR hot-update is ready and the type
    /// itself is missing (a game patch renamed/removed it) — report unavailable, and the caller should CACHE this
    /// negative under its TTL, so later probes skip re-resolving it.</summary>
    DefinitivelyUnavailable,
}

/// <summary>Decides a <see cref="ProbeOutcome"/> from the two facts a per-layer probe already has in hand.</summary>
internal static class VisibilityProbeDecision
{
    /// <param name="typeLoaded">The hot-update type was found (<c>IGameTypeRegistry.FindType</c> returned non-null).</param>
    /// <param name="memberFound">Only meaningful when <paramref name="typeLoaded"/> is true: whether the expected
    /// reflection member (method/property) resolved on that type.</param>
    /// <param name="hotUpdateReady">All hot-update assemblies have loaded: a type still missing now will not appear.</param>
    public static ProbeOutcome Decide(bool typeLoaded, bool memberFound, bool hotUpdateReady = false)
    {
        if (!typeLoaded) return hotUpdateReady ? ProbeOutcome.DefinitivelyUnavailable : ProbeOutcome.Optimistic;
        return memberFound ? ProbeOutcome.Available : ProbeOutcome.DefinitivelyUnavailable;
    }
}
