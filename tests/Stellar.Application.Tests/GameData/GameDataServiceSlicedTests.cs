using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

/// <summary>
/// Pins the time-sliced game-data contract (raid 2026-11-10 fw:gamedata spikes): a Pending step leaves the
/// caches and the queue position untouched, and each table lands in one piece once its step reports Done.
/// </summary>
public sealed class GameDataServiceSlicedTests
{
    /// <summary>Probe whose every eager / per-table step reports Pending a set number of times first.</summary>
    private sealed class SlicedProbe : IGameDataProbe
    {
        public int PendingStepsPerLoad { get; init; }
        public GameDataEagerSnapshot EagerSnapshot { get; init; }
        public Dictionary<GameDataTableKind, object> Caches { get; } = new();
        public List<GameDataTableKind> StepCalls { get; } = new();
        private int _eagerPending = -1;
        private readonly Dictionary<GameDataTableKind, int> _tablePending = new();

        public bool TryLoadEager(out GameDataEagerSnapshot snapshot) { snapshot = EagerSnapshot; return true; }

        public bool TryLoadOne(GameDataTableKind kind, out object cache) => Caches.TryGetValue(kind, out cache!);

        public GameDataStep StepEager(long deadline, out GameDataEagerSnapshot snapshot)
        {
            snapshot = default;
            if (_eagerPending < 0) _eagerPending = PendingStepsPerLoad;
            if (_eagerPending-- > 0) return GameDataStep.Pending;
            snapshot = EagerSnapshot;
            return GameDataStep.Done;
        }

        public GameDataStep StepOne(GameDataTableKind kind, long deadline, out object cache)
        {
            StepCalls.Add(kind);
            cache = null!;
            if (!_tablePending.TryGetValue(kind, out var left)) left = PendingStepsPerLoad;
            _tablePending[kind] = left - 1;
            if (left > 0) return GameDataStep.Pending;
            return Caches.TryGetValue(kind, out cache!) ? GameDataStep.Done : GameDataStep.Failed;
        }
    }

    private static GameDataEagerSnapshot Snapshot() => new()
    {
        Skills = new Dictionary<int, SkillInfo>(),
        SkillLevelToBase = new Dictionary<int, int>(),
        Buffs = new Dictionary<int, BuffInfo>(),
        Professions = new Dictionary<int, ProfessionInfo> { [1] = new ProfessionInfo(1, "Frost Mage", "i.png", false, new[] { 1 }) },
        Attributes = new Dictionary<int, AttributeInfo>(),
        AttributeProfiles = new Dictionary<int, AttributeProfileInfo>(),
        Items = new Dictionary<int, ItemInfo>(),
    };

    [Fact]
    public void StepEager_StaysUnavailableUntilTheBatchIsDone_ThenAppliesAtOnce()
    {
        var svc = new GameDataService(new CombatEntityTracker());
        var probe = new SlicedProbe { PendingStepsPerLoad = 2, EagerSnapshot = Snapshot() };

        Assert.False(svc.StepEager(probe, deadline: 0));
        Assert.False(svc.StepEager(probe, deadline: 0));
        Assert.False(svc.IsAvailable);
        Assert.Null(svc.Combat.GetProfession(1));

        Assert.True(svc.StepEager(probe, deadline: 0));
        Assert.True(svc.IsAvailable);
        Assert.Equal("Frost Mage", svc.Combat.GetProfession(1)!.Value.Name);
    }

    [Fact]
    public void DrainDeferred_PendingTable_IsResumedNotSkipped_AndLandsWhole()
    {
        var svc = new GameDataService(new CombatEntityTracker());
        var probe = new SlicedProbe { PendingStepsPerLoad = 2, EagerSnapshot = Snapshot() };
        probe.Caches[GameDataTableKind.Talent] = new Dictionary<int, TalentInfo> { [7] = new TalentInfo(7, "Icicle Spec", "", "", 1) };
        svc.LoadEager(probe);

        svc.DrainDeferred(probe, deadline: 0);
        svc.DrainDeferred(probe, deadline: 0);
        Assert.Null(svc.Combat.GetTalent(7));                       // still in flight
        Assert.Equal(new[] { GameDataTableKind.Talent, GameDataTableKind.Talent }, probe.StepCalls);

        svc.DrainDeferred(probe, deadline: 0);
        Assert.Equal("Icicle Spec", svc.Combat.GetTalent(7)!.Value.Name);

        svc.DrainDeferred(probe, deadline: 0);                      // the queue moved on to the next kind
        Assert.Equal(GameDataTableKind.DamageAttr, probe.StepCalls[^1]);
    }

    [Fact]
    public void DrainDeferred_DefaultStepOne_KeepsTheOneTablePerCallContract()
    {
        // A probe without its own StepOne (the default interface method) loads the table in one call.
        var (svc, probe) = (new GameDataService(new CombatEntityTracker()), new StubGameDataProbe());
        probe.EagerSnapshot = Snapshot();
        probe.DeferredCaches[GameDataTableKind.Talent] = new Dictionary<int, TalentInfo> { [7] = new TalentInfo(7, "Icicle Spec", "", "", 1) };
        svc.LoadEager(probe);

        svc.DrainDeferred(probe, deadline: 0);

        Assert.Equal(new[] { GameDataTableKind.Talent }, probe.CallOrder);
        Assert.Equal("Icicle Spec", svc.Combat.GetTalent(7)!.Value.Name);
    }
}
