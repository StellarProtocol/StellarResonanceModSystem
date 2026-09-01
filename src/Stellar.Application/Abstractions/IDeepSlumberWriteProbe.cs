using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound port (implemented in Infrastructure over the game's <c>season_talent</c> Lua VM;
/// InternalsVisibleTo grants access) for the primitive Deep-Slumber writes. Each returns the game's
/// bare error code — <c>0</c> = ok. The <c>currentItemId</c> on unsocket is toast-only (the server
/// request carries the nodeId alone).</summary>
internal interface IDeepSlumberWriteProbe
{
    bool IsResolved { get; }
    Task<int> EnableLineAsync(int areaId, CancellationToken ct);
    /// <summary>Reset every anchor + factor of one area to inactive (the game has no per-node anchor
    /// removal — the only way to remove an anchor is a whole-area reset). Refunds consumed items and
    /// returns every socketed factor to the bag; costs the game's reset currency.</summary>
    Task<int> ResetNodesAsync(int areaId, CancellationToken ct);
    /// <summary>Activate one normal node ("Anchor of the Mind") in the currently-active area.</summary>
    Task<int> ActivateNodeAsync(int nodeId, CancellationToken ct);
    Task<int> SocketFactorAsync(int nodeId, int itemId, CancellationToken ct);
    Task<int> UnsocketFactorAsync(int nodeId, int currentItemId, CancellationToken ct);
}

/// <summary>The non-game sentinel codes an <see cref="IDeepSlumberWriteProbe"/> completes an op with
/// when it cannot obtain a real server reply — the single source of truth shared by the probe (which
/// produces them) and <c>DeepSlumberService</c> (which decides retry on them). A <b>transient</b> code
/// means the request did NOT land on the server (never dispatched, or dropped with no reply), so
/// re-firing it is idempotency-safe; a <i>positive</i> game <c>EErrorCode</c> (e.g. 7555 empty node,
/// 7561 item unavailable, a combat refusal) is a DETERMINISTIC refusal and must NEVER be retried —
/// re-firing would fail identically, and on a succeeded-but-reply-lost op it would misreport success as
/// failure.</summary>
internal static class DeepSlumberWriteCode
{
    public const int Ok = 0;
    public const int Unavailable = -1;   // bridge unresolved / Lua dispatch failed — never reached the server
    public const int Timeout = -2;       // dispatched, but no reply within the completion timeout — dropped
    public const int Cancelled = -3;     // caller's token fired

    /// <summary>True for codes that mean "the request did not land" — the only codes safe to retry.</summary>
    public static bool IsTransient(int code) => code == Unavailable || code == Timeout;
}
