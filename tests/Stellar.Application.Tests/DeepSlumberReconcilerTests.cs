using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class DeepSlumberReconcilerTests
{
    private static DeepSlumberState State(int areaId, bool active, params (int node, int item)[] mids)
    {
        var midList = new List<int[]>();
        foreach (var m in mids) midList.Add(new[] { m.node, m.item });
        var area = new DeepSlumberArea(areaId, active, 0, new List<int[]>(), midList, new List<int[]>());
        var line = new DeepSlumberLine(3, 800522, new List<DeepSlumberArea> { area });
        return new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine> { line });
    }

    private static DeepSlumberSetup Setup(int areaId, params (int node, int item)[] factors)
    {
        var f = new List<int[]>();
        foreach (var x in factors) f.Add(new[] { x.node, x.item });
        return new DeepSlumberSetup(1, new List<DeepSlumberAreaBinding> { new(areaId, f) });
    }

    [Fact]
    public void IdenticalState_PlansNothing()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, true, (118, 20020001)), Setup(5, (118, 20020001)));
        Assert.Empty(ops);
    }

    [Fact]
    public void EmptyLiveSocket_NotUnsocketed()
    {
        // The live area carries UNLOCKED-but-EMPTY middle sockets (itemId 0). An empty socket is not a
        // factor — the reconciler must NOT try to unsocket it (owner smoke 2026-08-24: unsocket on an
        // empty socket → code 7555 → "partly applied"). Target wants 0 factors here.
        var ops = DeepSlumberReconciler.Plan(State(5, true, (143, 0), (146, 0)), Setup(5));
        Assert.DoesNotContain(ops, o => o.Kind == DeepSlumberOpKind.UnsocketFactor);
    }

    [Fact]
    public void SharedFactorMovingNodes_FreesItemBeforeSocketing()
    {
        // Live area 5 holds scarce item 20020964 at node 141; the target wants it at node 140. Factor
        // items are single-copy, so the reconciler must unsocket 141 (return it to the bag) BEFORE
        // socketing 140 (owner smoke 2026-08-24: socket-before-unsocket → item unavailable → code 7561
        // → "partly applied").
        var ops = DeepSlumberReconciler.Plan(State(5, true, (141, 20020964)), Setup(5, (140, 20020964))).ToList();
        var freeIdx = ops.FindIndex(o => o.Kind == DeepSlumberOpKind.UnsocketFactor && o.Key == 141);
        var socketIdx = ops.FindIndex(o => o.Kind == DeepSlumberOpKind.SocketFactor && o.Key == 140);
        Assert.True(freeIdx >= 0 && socketIdx >= 0, "expected both a free-141 and a socket-140 op");
        Assert.True(freeIdx < socketIdx, "must unsocket the shared item before socketing it elsewhere");
    }

    [Fact]
    public void InactiveTargetArea_EmitsEnableLineFirst()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, false), Setup(5));
        Assert.Equal(DeepSlumberOp.EnableLine(5), ops[0]);
    }

    [Fact]
    public void CrossSeasonAreaIdCollision_MatchesCurrentSeasonOnly()
    {
        // AreaIds are REUSED across seasons: prior season (lineId 2) area 7 is fully built; the current
        // season (lineId 3) area 7 is active + empty. Matching by AreaId alone would strip the prior
        // season's factors (owner smoke 2026-08-24: 18 bogus unsockets → every one code 7555). Target =
        // current-season area 7, 0 factors → the reconciler must diff the CURRENT (empty) area 7 and
        // emit NO unsocket ops for the prior season's factors.
        var oldArea = new DeepSlumberArea(7, true, 0, new List<int[]>(),
            new List<int[]> { new[] { 160, 500 }, new[] { 161, 501 } }, new List<int[]>());
        var newArea = new DeepSlumberArea(7, true, 0, new List<int[]>(), new List<int[]>(), new List<int[]>());
        var state = new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine>
        {
            new(2, 800522, new List<DeepSlumberArea> { oldArea }),
            new(3, 800522, new List<DeepSlumberArea> { newArea }),
        });
        var target = new DeepSlumberSetup(1, new List<DeepSlumberAreaBinding> { new(7, new List<int[]>()) });
        var ops = DeepSlumberReconciler.Plan(state, target);
        Assert.DoesNotContain(ops, o => o.Kind == DeepSlumberOpKind.UnsocketFactor);
    }

    [Fact]
    public void AlreadyCorrectFactor_CurrentSeason_PlansNothing()
    {
        // Prior season area 7 has a DIFFERENT factor at the same node; the current season area 7 already
        // holds the wanted factor. Must read as "already matches" → no unsocket+resocket churn (owner:
        // switching back re-sockets the same factor, wasting resources).
        var oldArea = new DeepSlumberArea(7, true, 0, new List<int[]>(),
            new List<int[]> { new[] { 118, 999 } }, new List<int[]>());
        var newArea = new DeepSlumberArea(7, true, 0, new List<int[]>(),
            new List<int[]> { new[] { 118, 20020001 } }, new List<int[]>());
        var state = new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine>
        {
            new(2, 800522, new List<DeepSlumberArea> { oldArea }),
            new(3, 800522, new List<DeepSlumberArea> { newArea }),
        });
        var ops = DeepSlumberReconciler.Plan(state, Setup(7, (118, 20020001)));
        Assert.Empty(ops);
    }

    [Fact]
    public void EmptyNode_EmitsSocket()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, true), Setup(5, (118, 20020001)));
        Assert.Contains(DeepSlumberOp.Socket(118, 20020001), ops);
        Assert.DoesNotContain(ops, o => o.Kind == DeepSlumberOpKind.UnsocketFactor);
    }

    [Fact]
    public void DifferentFactor_EmitsUnsocketThenSocket()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, true, (118, 111)), Setup(5, (118, 222))).ToList();
        var i = ops.IndexOf(DeepSlumberOp.Unsocket(118, 111));
        var j = ops.IndexOf(DeepSlumberOp.Socket(118, 222));
        Assert.True(i >= 0 && j >= 0 && i < j);
    }

    [Fact]
    public void FactorAbsentFromTarget_EmitsUnsocket()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, true, (118, 111)), Setup(5));
        Assert.Contains(DeepSlumberOp.Unsocket(118, 111), ops);
    }

    [Fact]
    public void EnableLinePrecedesFactorOps()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, false), Setup(5, (118, 222)));
        Assert.Equal(DeepSlumberOpKind.EnableLine, ops[0].Kind);
        Assert.Contains(ops, o => o.Kind == DeepSlumberOpKind.SocketFactor);
    }
}
