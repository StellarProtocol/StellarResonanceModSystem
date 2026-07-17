using System;
using System.Collections.Generic;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Covers the flow_info (field 2) dirty-container capture added to
/// <see cref="DungeonDirtyDataReader"/> for method-24 mid-run flow-state
/// transitions: field 1 (<c>state</c>, <c>EDungeonState</c>) alongside the
/// pre-existing field 8 (<c>result</c>). Builder helpers mirror
/// <c>DungeonDirtyDataReaderTests</c> verbatim (int32-LE
/// <c>[-2][size][fieldIndex payload]…[-3]</c> container framing) so both test
/// files exercise the identical blob shape the reader consumes.
/// </summary>
public sealed class DungeonDirtyFlowStateTests
{
    private const int TagBegin = -2;
    private const int TagEnd = -3;

    [Fact]
    public void FlowContainer_StateOnly_HasFlowStateTrue_ResultDefaultsToZero()
    {
        var blob = Container(Field(2, Container(Field(1, I32(3)))));

        Assert.True(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out var result));
        Assert.True(result.HasFlowState);
        Assert.Equal(3, result.FlowState);
        Assert.True(result.HasFlowResult);   // container present
        Assert.Equal(0, result.FlowResult);
    }

    [Fact]
    public void FlowContainer_ResultOnly_HasFlowStateFalse_DoesNotPushPhantomZero()
    {
        var blob = Container(Field(2, Container(Field(8, I32(2)))));

        Assert.True(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out var result));
        Assert.False(result.HasFlowState);
        Assert.True(result.HasFlowResult);
        Assert.Equal(2, result.FlowResult);
    }

    [Fact]
    public void FlowContainer_BothStateAndResult_BothSurfaced()
    {
        var blob = Container(Field(2, Container(Field(1, I32(4)), Field(8, I32(1)))));

        Assert.True(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out var result));
        Assert.True(result.HasFlowState);
        Assert.Equal(4, result.FlowState);
        Assert.True(result.HasFlowResult);
        Assert.Equal(1, result.FlowResult);
    }

    [Fact]
    public void EmptyFlowContainer_HasFlowStateFalse_RegressionPin()
    {
        // Present-but-empty flow container ([-2][-3]) — existing behavior
        // (DungeonDirtyDataReader.cs:211) sets HasFlowResult but must NOT set
        // HasFlowState (there is no state entry to observe).
        var blob = Container(Field(2, EmptyContainer()));

        Assert.True(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out var result));
        Assert.False(result.HasFlowState);
        Assert.True(result.HasFlowResult);
        Assert.Equal(0, result.FlowResult);
    }

    // ---- payload builders (verbatim from DungeonDirtyDataReaderTests) ----

    // Full container framing: [-2][size][entries…][-3], size = entry bytes only.
    private static byte[] Container(params byte[][] entries)
    {
        var body = Bytes(entries);
        return Bytes(I32(TagBegin), I32(body.Length), body, I32(TagEnd));
    }

    // Empty container short form: [-2][-3].
    private static byte[] EmptyContainer() => Bytes(I32(TagBegin), I32(TagEnd));

    private static byte[] Field(int index, byte[] payload) => Bytes(I32(index), payload);

    private static byte[] I32(int v) => BitConverter.GetBytes(v);

    private static byte[] Bytes(params byte[][] parts)
    {
        var b = new List<byte>();
        foreach (var p in parts) b.AddRange(p);
        return b.ToArray();
    }
}
