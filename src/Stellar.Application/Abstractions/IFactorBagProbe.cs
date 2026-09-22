using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the free (un-socketed) inventory count of Deep-Slumber phantom
/// factors. The game consumes a factor from the bag on socket and refunds it on unsocket/reset, so
/// these counts are exactly the copies available before an apply's socket phase — used by
/// <see cref="Stellar.Application.Services.DeepSlumberReconciler"/> to avoid raiding a factor from an
/// inactive loadout when a spare is already on hand. Implemented in Infrastructure.</summary>
internal interface IFactorBagProbe
{
    /// <summary>Free inventory copies of each requested factor itemId (absent ⇒ zero). Empty when the
    /// live container is unresolved — the reconciler then falls back to its conservative behaviour.</summary>
    IReadOnlyDictionary<int, int> ReadFactorBagCounts(IReadOnlyCollection<int> itemIds);
}

/// <summary>Null-object bag probe (always empty ⇒ "counts unknown"). The reconciler treats an empty bag
/// conservatively, so a service constructed without a real probe keeps the pre-2026-09-22 behaviour.</summary>
internal sealed class EmptyFactorBagProbe : IFactorBagProbe
{
    public static readonly EmptyFactorBagProbe Instance = new();
    private static readonly IReadOnlyDictionary<int, int> Empty = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> ReadFactorBagCounts(IReadOnlyCollection<int> itemIds) => Empty;
}
