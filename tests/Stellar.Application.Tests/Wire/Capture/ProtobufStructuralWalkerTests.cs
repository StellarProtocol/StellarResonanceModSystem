using System.Linq;
using Stellar.Infrastructure.Game.Capture;
using Xunit;

namespace Stellar.Application.Tests.Wire.Capture;

public sealed class ProtobufStructuralWalkerTests
{
    [Fact]
    public void Walk_VarintAndString_ClassifiesFields()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(150)
            .Tag(2, 2).String("hi")
            .ToArray();

        var node = ProtobufStructuralWalker.Walk(bytes);

        Assert.False(node.Truncated);
        var f1 = node.Fields.Single(f => f.FieldNumber == 1);
        Assert.Equal(ProtoKind.Varint, f1.Kind);
        Assert.Equal(150ul, f1.VarintValue);
        var f2 = node.Fields.Single(f => f.FieldNumber == 2);
        Assert.Equal(ProtoKind.String, f2.Kind);
        Assert.Equal("hi", f2.StringValue);
    }

    [Fact]
    public void Walk_NestedMessage_Recurses()
    {
        var inner = new WireBytes().Tag(1, 0).Varint(7).ToArray();
        var outer = new WireBytes().Tag(3, 2).LengthDelimited(inner).ToArray();

        var node = ProtobufStructuralWalker.Walk(outer);

        var f3 = node.Fields.Single(f => f.FieldNumber == 3);
        Assert.Equal(ProtoKind.Message, f3.Kind);
        Assert.Equal(7ul, f3.Message!.Fields.Single().VarintValue);
    }

    [Fact]
    public void Walk_RepeatedField_GroupsWithCount()
    {
        var bytes = new WireBytes()
            .Tag(2, 2).String("a")
            .Tag(2, 2).String("b")
            .Tag(2, 2).String("c")
            .ToArray();

        var node = ProtobufStructuralWalker.Walk(bytes);

        Assert.Equal(3, node.Fields.Count(f => f.FieldNumber == 2));
    }

    [Fact]
    public void Walk_NonUtf8LengthDelimited_IsBytes()
    {
        var bytes = new WireBytes().Tag(4, 2).LengthDelimited(new byte[] { 0xFF, 0xFE, 0x00 }).ToArray();

        var node = ProtobufStructuralWalker.Walk(bytes);

        var f4 = node.Fields.Single(f => f.FieldNumber == 4);
        Assert.Equal(ProtoKind.Bytes, f4.Kind);
        Assert.Equal(3, f4.ByteLength);
    }

    [Fact]
    public void Walk_Malformed_ReturnsPartialNotThrow()
    {
        var bytes = new WireBytes().Tag(1, 2).Varint(10).Raw(0x01).Raw(0x02).ToArray();

        var node = ProtobufStructuralWalker.Walk(bytes);

        Assert.True(node.Truncated);
    }

    [Fact]
    public void Walk_DepthGuard_StopsAndMarksTruncated()
    {
        byte[] cur = new WireBytes().Tag(1, 0).Varint(1).ToArray();
        for (int i = 0; i < 50; i++)
            cur = new WireBytes().Tag(1, 2).LengthDelimited(cur).ToArray();

        var node = ProtobufStructuralWalker.Walk(cur);

        Assert.True(node.Truncated);
    }

    [Fact]
    public void Walk_Utf8CjkString_IsClassifiedAsString()
    {
        var name = System.Text.Encoding.UTF8.GetBytes("张三");
        var bytes = new WireBytes().Tag(1, 2).LengthDelimited(name).ToArray();

        var node = ProtobufStructuralWalker.Walk(bytes);

        var f1 = node.Fields.Single(f => f.FieldNumber == 1);
        Assert.Equal(ProtoKind.String, f1.Kind);
        Assert.Equal("张三", f1.StringValue);
    }
}
