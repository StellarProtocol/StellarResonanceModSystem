using System.Collections.Generic;

namespace Stellar.Infrastructure.Game.Capture;

internal enum ProtoKind { Varint, Fixed32, Fixed64, String, Bytes, Message }

internal sealed class ProtoNode
{
    public List<ProtoField> Fields { get; } = new();
    public bool Truncated { get; set; }

    /// <summary>
    /// Set when <see cref="Truncated"/> was caused by exceeding the recursion-depth
    /// cap rather than a malformed-bytes parse failure. Used by
    /// <c>ProtobufStructuralWalker</c> to propagate depth truncation upward through
    /// all nesting levels.
    /// </summary>
    internal bool DepthTruncated { get; set; }
}

internal sealed class ProtoField
{
    public int FieldNumber { get; init; }
    public ProtoKind Kind { get; init; }
    public ulong VarintValue { get; init; }
    public ulong FixedValue { get; init; }
    public string? StringValue { get; init; }
    public int ByteLength { get; init; }
    public ProtoNode? Message { get; init; }
}
