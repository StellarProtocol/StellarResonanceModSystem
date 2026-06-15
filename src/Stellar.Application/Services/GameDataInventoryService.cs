using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class GameDataInventoryService : IGameDataInventory
{
    // Each cache is a Dictionary<int, TInfo> read lock-free via Volatile.Read
    // and replaced atomically via Volatile.Write. Build happens on the game
    // thread; reads happen on any thread.
    private IReadOnlyDictionary<int, ItemInfo>?   _items;
    private IReadOnlyDictionary<int, EquipInfo>?  _equips;
    private IReadOnlyDictionary<int, WeaponInfo>? _weapons;

    public ItemInfo?   GetItem(int id)   => TryGet(Volatile.Read(ref _items), id);
    public EquipInfo?  GetEquip(int id)  => TryGet(Volatile.Read(ref _equips), id);
    public WeaponInfo? GetWeapon(int id) => TryGet(Volatile.Read(ref _weapons), id);

    internal void LoadItems(IReadOnlyDictionary<int, ItemInfo> cache)
        => Volatile.Write(ref _items, cache);
    internal void LoadEquips(IReadOnlyDictionary<int, EquipInfo> cache)
        => Volatile.Write(ref _equips, cache);
    internal void LoadWeapons(IReadOnlyDictionary<int, WeaponInfo> cache)
        => Volatile.Write(ref _weapons, cache);

    private static T? TryGet<T>(IReadOnlyDictionary<int, T>? cache, int id) where T : struct
    {
        if (cache is null) return null;
        return cache.TryGetValue(id, out var info) ? info : null;
    }
}
