// tests/Stellar.Application.Tests/PlayerStats/StubPlayerStatsProbe.cs
using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.PlayerStats;

internal sealed class StubPlayerStatsProbe : IPlayerStatsProbe
{
    public bool TrySampleReturns { get; set; } = true;
    public IReadOnlyDictionary<int, long> NextValues { get; set; }
        = new Dictionary<int, long>();

    public List<int[]> SubscribedSnapshots { get; } = new();

    public bool TrySample(IReadOnlyCollection<int> subscribed,
                          out IReadOnlyDictionary<int, long> values)
    {
        // Capture a copy of the subscribed set per call for assertion.
        var snap = new int[subscribed.Count];
        var i = 0;
        foreach (var id in subscribed) snap[i++] = id;
        SubscribedSnapshots.Add(snap);

        values = TrySampleReturns ? NextValues : new Dictionary<int, long>();
        return TrySampleReturns;
    }
}
