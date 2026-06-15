using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.GameData;

internal sealed class StubGameDataProbe : IGameDataProbe
{
    public bool TryLoadEagerReturns { get; set; } = true;
    public GameDataEagerSnapshot EagerSnapshot { get; set; }

    public Dictionary<GameDataTableKind, object?> DeferredCaches { get; }
        = new Dictionary<GameDataTableKind, object?>();

    public List<GameDataTableKind> CallOrder { get; } = new List<GameDataTableKind>();

    public bool TryLoadEager(out GameDataEagerSnapshot snapshot)
    {
        snapshot = EagerSnapshot;
        return TryLoadEagerReturns;
    }

    public bool TryLoadOne(GameDataTableKind kind, out object cache)
    {
        CallOrder.Add(kind);
        if (DeferredCaches.TryGetValue(kind, out var value) && value is not null)
        {
            cache = value;
            return true;
        }
        cache = null!;
        return false;
    }
}
