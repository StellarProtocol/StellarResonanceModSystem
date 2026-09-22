using System.Collections.Generic;
using System.Reflection;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>Free (un-socketed) inventory counts of Deep-Slumber phantom factors, for the cross-loadout
/// reconciler's bag-aware free (owner 2026-09-22 — never strip a shared factor from an inactive loadout
/// when a spare is already on hand). The game CONSUMES a factor from the item bag on socket and REFUNDS
/// it on unsocket/reset, so the item packages hold exactly the free copies — a factor socketed in ANY
/// tree is absent from the bag. Reuses the same reflective ItemPackage → Packages → Items walk as the
/// module/gear readers; fully nil-safe and passive (never drives resolution). Implements the
/// Application-internal <see cref="IFactorBagProbe"/> (surfaced through <see cref="PandaInventoryProbe"/>).</summary>
internal sealed partial class PandaInventoryPullReader
{
    // ItemContainerArchive stack size — "Count" or "Num" depending on the generated proto, resolved off
    // the live entry type on first hit; absent ⇒ one copy per entry (each uuid is its own instance).
    private PropertyInfo? _itemCountProperty;
    private bool _itemCountResolved;

    internal IReadOnlyDictionary<int, int> ReadFactorBagCounts(IReadOnlyCollection<int> itemIds)
    {
        var counts = new Dictionary<int, int>();
        if (itemIds.Count == 0) return counts;

        var charSerialize = TryGetLiveCharSerialize();
        if (charSerialize is null) return counts;
        object? itemPackage = SafeGet(_itemPackageProperty, charSerialize);
        if (itemPackage is null) return counts;
        object? packagesMap = SafeGet(_packagesProperty, itemPackage);
        if (packagesMap is null) return counts;

        var wanted = itemIds as HashSet<int> ?? new HashSet<int>(itemIds);
        foreach (var package in EnumerateMapValues(packagesMap))
        {
            if (package is null) continue;
            _packageItemsProperty ??= FindMapLikeProperty(package.GetType(), "Items");
            object? itemsMap = SafeGet(_packageItemsProperty, package);
            if (itemsMap is null) continue;
            foreach (var entry in EnumerateMapValues(itemsMap))
                AccumulateFactorCount(entry, wanted, counts);
        }
        OnFactorBagCountsLogged(counts);
        return counts;
    }

    private void AccumulateFactorCount(object? entry, HashSet<int> wanted, Dictionary<int, int> counts)
    {
        if (entry is null) return;
        _itemConfigIdProperty ??= entry.GetType().GetProperty("ConfigId", AnyInstance);
        int configId = TryReadInt32(entry, _itemConfigIdProperty);
        if (!wanted.Contains(configId)) return;
        int n = ReadStackCount(entry);
        counts[configId] = (counts.TryGetValue(configId, out var c) ? c : 0) + n;
    }

    private int ReadStackCount(object entry)
    {
        if (!_itemCountResolved)
        {
            var t = entry.GetType();
            _itemCountProperty = t.GetProperty("Count", AnyInstance) ?? t.GetProperty("Num", AnyInstance);
            _itemCountResolved = true;
        }
        if (_itemCountProperty is null) return 1;   // no stack field ⇒ one copy per entry
        int n = TryReadInt32(entry, _itemCountProperty);
        return n > 0 ? n : 1;                        // a present entry is at least one copy
    }
}
