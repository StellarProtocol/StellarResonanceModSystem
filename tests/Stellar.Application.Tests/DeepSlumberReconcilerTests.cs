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
    public void InactiveTargetArea_EmitsEnableLineFirst()
    {
        var ops = DeepSlumberReconciler.Plan(State(5, false), Setup(5));
        Assert.Equal(DeepSlumberOp.EnableLine(5), ops[0]);
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
