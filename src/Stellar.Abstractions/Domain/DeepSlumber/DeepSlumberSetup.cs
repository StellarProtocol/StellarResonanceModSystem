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

/// <summary>One area of a bound setup: the area to enable and its middle-node phantom-factor sockets.</summary>
/// <param name="AreaId">The area id to enable (the <c>zoneId</c> passed to the game's line switch).</param>
/// <param name="Factors">[nodeId, itemId] middle-node factor sockets, sorted by nodeId.</param>
public sealed record DeepSlumberAreaBinding(
    int AreaId,
    IReadOnlyList<int[]> Factors);
