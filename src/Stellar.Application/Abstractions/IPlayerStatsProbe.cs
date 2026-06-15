// src/Stellar.Application/Abstractions/IPlayerStatsProbe.cs
using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — Application asks Infrastructure to sample live
/// attribute values for the subscribed set of attribute IDs. Implemented in
/// <c>Stellar.Infrastructure</c> by walking the live Panda hot-update objects
/// via <c>ZEntity.TryGetAttr&lt;T&gt;(Zproto.EAttrType, out T)</c>.
/// </summary>
internal interface IPlayerStatsProbe
{
    /// <summary>
    /// Samples values for each ID in <paramref name="subscribed"/>. Returns
    /// false if the player isn't loaded yet; <paramref name="values"/> is set
    /// to an empty dictionary in that case.
    /// </summary>
    bool TrySample(
        IReadOnlyCollection<int> subscribed,
        out IReadOnlyDictionary<int, long> values);
}
