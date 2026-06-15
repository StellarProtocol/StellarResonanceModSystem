using System;
using System.Text;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>
/// Schema-less protobuf decoder: walks wire bytes into a field tree, recursing
/// length-delimited fields that parse cleanly as nested messages. No schema needed.
/// </summary>
internal static class ProtobufStructuralWalker
{
    private const int MaxNodes = 4096;
    private const int MaxDepth = 16;

    /// <summary>
    /// Sentinel returned by <see cref="Walker.TryWalkAsMessage"/> exclusively when
    /// the recursion-depth cap was hit. Lets <see cref="Walker.ReadLd"/> propagate
    /// depth truncation all the way to the outermost node.
    /// </summary>
    private static readonly ProtoNode DepthExceeded = new() { Truncated = true, DepthTruncated = true };

    public static ProtoNode Walk(ReadOnlySpan<byte> payload)
        => new Walker().WalkInner(payload, 0);

    /// <summary>
    /// Bundles the decoded tag + current recursion depth into one param so
    /// dispatch methods stay within the 5-parameter limit.
    /// </summary>
    private readonly struct FieldTag
    {
        public readonly int Field;
        public readonly int Wire;
        public readonly int Depth;

        public FieldTag(int field, int wire, int depth)
        {
            Field = field;
            Wire = wire;
            Depth = depth;
        }
    }

    /// <summary>
    /// Stateful walker; <see cref="_nodeBudget"/> is shared across all recursion
    /// levels so the total node count is bounded globally.
    /// </summary>
    private sealed class Walker
    {
        private int _nodeBudget = MaxNodes;

        internal ProtoNode WalkInner(ReadOnlySpan<byte> data, int depth)
        {
            var node = new ProtoNode();
            int pos = 0;
            while (pos < data.Length)
            {
                if (_nodeBudget-- <= 0) { node.Truncated = true; return node; }
                if (depth >= MaxDepth) { node.Truncated = true; node.DepthTruncated = true; return node; }
                if (!WireProtocol.TryReadTag(data, ref pos, out int field, out int wire))
                { node.Truncated = true; return node; }
                var tag = new FieldTag(field, wire, depth);
                if (!ReadValue(data, ref pos, node, tag)) { node.Truncated = true; return node; }
            }
            return node;
        }

        private bool ReadValue(ReadOnlySpan<byte> data, ref int pos, ProtoNode node, FieldTag tag)
        {
            switch (tag.Wire)
            {
                case 0:
                    if (!WireProtocol.TryReadVarint(data, ref pos, out var v)) return false;
                    node.Fields.Add(new ProtoField { FieldNumber = tag.Field, Kind = ProtoKind.Varint, VarintValue = v });
                    return true;
                case 1:
                    if (pos + 8 > data.Length) return false;
                    node.Fields.Add(new ProtoField { FieldNumber = tag.Field, Kind = ProtoKind.Fixed64, FixedValue = ReadFixed(data, pos, 8) });
                    pos += 8; return true;
                case 5:
                    if (pos + 4 > data.Length) return false;
                    node.Fields.Add(new ProtoField { FieldNumber = tag.Field, Kind = ProtoKind.Fixed32, FixedValue = ReadFixed(data, pos, 4) });
                    pos += 4; return true;
                case 2:
                    return ReadLd(data, ref pos, node, tag);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles wire-type-2 fields. Classifies as Message, String, or Bytes.
        /// Returns false when the <see cref="DepthExceeded"/> sentinel is received,
        /// causing the parent node to be marked Truncated.
        /// </summary>
        private bool ReadLd(ReadOnlySpan<byte> data, ref int pos, ProtoNode node, FieldTag tag)
        {
            if (!WireProtocol.TryReadLengthDelimited(data, ref pos, out var inner)) return false;

            // Prefer UTF-8 printable text before attempting message parse; avoids
            // false positives where ASCII bytes accidentally satisfy the protobuf grammar.
            if (IsLikelyText(inner))
            {
                node.Fields.Add(new ProtoField { FieldNumber = tag.Field, Kind = ProtoKind.String, StringValue = System.Text.Encoding.UTF8.GetString(inner) });
                return true;
            }

            var sub = TryWalkAsMessage(inner, tag.Depth + 1);

            // Depth-exceeded sentinel: tag the node so WalkInner can propagate upward.
            if (ReferenceEquals(sub, DepthExceeded)) { node.DepthTruncated = true; return false; }

            if (sub is not null)
            {
                node.Fields.Add(new ProtoField { FieldNumber = tag.Field, Kind = ProtoKind.Message, Message = sub });
                return true;
            }

            node.Fields.Add(new ProtoField { FieldNumber = tag.Field, Kind = ProtoKind.Bytes, ByteLength = inner.Length });
            return true;
        }

        /// <summary>
        /// Returns a <see cref="ProtoNode"/> on clean parse, <c>null</c> when
        /// <paramref name="inner"/> is empty or does not parse as valid protobuf,
        /// or the <see cref="DepthExceeded"/> sentinel when depth truncation propagates
        /// upward from any nested level.
        /// </summary>
        private ProtoNode? TryWalkAsMessage(ReadOnlySpan<byte> inner, int depth)
        {
            if (inner.Length == 0) return null;
            if (depth >= MaxDepth) return DepthExceeded;
            var sub = WalkInner(inner, depth);
            // Depth-truncated sub: propagate the sentinel upward.
            if (sub.DepthTruncated) return DepthExceeded;
            // Parse-failed or empty sub: treat as opaque bytes.
            if (sub.Truncated || sub.Fields.Count == 0) return null;
            return sub;
        }

        private static bool IsLikelyText(ReadOnlySpan<byte> b)
        {
            if (b.Length == 0) return true;
            // Accept valid UTF-8 with no non-printable control characters.
            // Strict decode (throwOnInvalidBytes:true) rejects protobuf binary
            // (varint continuation bytes, packed numerics) which aren't valid UTF-8,
            // keeping false positives near zero — while CJK/Cyrillic/emoji names show as text.
            try
            {
                var s = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(b);
                foreach (var c in s)
                    if (c < '\t' || (c > '\r' && c < ' ')) return false;
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static ulong ReadFixed(ReadOnlySpan<byte> data, int pos, int n)
        {
            ulong r = 0;
            for (int i = 0; i < n; i++) r |= (ulong)data[pos + i] << (8 * i);
            return r;
        }
    }
}
