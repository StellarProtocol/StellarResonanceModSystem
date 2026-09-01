using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.DeepSlumber;

/// <summary>A bindable Deep-Slumber Psychoscope setup: the cultivate line/area(s) to enable and the
/// phantom factors socketed in their middle nodes. Captured from the live
/// <see cref="Stellar.Abstractions.Services.IDeepSlumber.GetState"/> and re-applied via
/// <see cref="Stellar.Abstractions.Services.IDeepSlumber.ApplySetupAsync"/>. Scope is line + factors
/// only — psycho-cards (big nodes) and normal-node allocations are deliberately excluded.</summary>
/// <param name="ProfessionId">The class this setup was captured under; the apply-time class guard.</param>
/// <param name="Areas">The areas that should be enabled, each with its phantom-factor sockets.</param>
public sealed record DeepSlumberSetup(
    int ProfessionId,
    IReadOnlyList<DeepSlumberAreaBinding> Areas);

/// <summary>One area of a bound setup: the area to enable, its middle-node phantom-factor sockets, and
/// the tree (normal-node "Anchors") that must be active.</summary>
/// <param name="AreaId">The area id to enable (the <c>zoneId</c> passed to the game's line switch).</param>
/// <param name="Factors">[nodeId, itemId] middle-node factor sockets, sorted by nodeId.</param>
public sealed record DeepSlumberAreaBinding(
    int AreaId,
    IReadOnlyList<int[]> Factors)
{
    /// <summary>The activated normal-node ("Anchor of the Mind") ids that make up this area's tree,
    /// sorted ascending. <c>null</c> means the tree was not captured (a legacy binding stored before tree
    /// capture existed) — the reconciler then leaves the live tree alone and reconciles factors only,
    /// never resetting. A non-null (possibly empty) list is the exact target tree: the reconciler resets +
    /// rebuilds when the live tree differs, since the game has no per-node anchor removal. The activeLevel
    /// is not stored — the game treats a normal node as presence-only. Added as a non-positional init
    /// member so the constructor stays binary-compatible with plugins built against ≤2.4.0.</summary>
    public IReadOnlyList<int>? NormalNodes { get; init; }
}
