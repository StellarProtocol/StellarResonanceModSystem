using System;
using System.Text;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game.Capture;

internal sealed partial class WirePacketCapture
{
    /// <summary>Bundles per-frame context to keep BuildLine within the 5-param limit.</summary>
    private readonly struct LineCtx
    {
        public string Dir { get; init; }
        public string Conn { get; init; }
        public bool Zstd { get; init; }
        public int Depth { get; init; }
        public ulong Svc { get; init; }
        public uint Method { get; init; }
        public string? SvcSource { get; init; }
    }

    private void ProcessCaptureFrame(CaptureFrame frame)
        => DecodeFrame(frame.Direction, frame.ConnId, frame.Bytes, depth: 0);

    private void DecodeFrame(string dir, string conn, ReadOnlySpan<byte> span, int depth)
    {
        if (span.Length < 6) return;
        ushort flags = (ushort)((span[4] << 8) | span[5]);
        ushort type = (ushort)(flags & 0x7FFF);
        bool zstd = (flags & 0x8000) != 0;

        if (type is 5 or 6) { DecodeWrapped(dir, conn, span, depth, zstd); return; }
        if (span.Length < 14) return;

        if (!WireFrameView.TryParse(span, type, zstd, out var view)) return;

        // NoteCall runs unconditionally so Returns can always be correlated,
        // even if the Call line itself is filtered out.
        if (view.Kind == WireMessageKind.Call)
            _correlator.NoteCall(view.CallId, view.ServiceUuid, view.MethodId);

        var ctx = BuildContext(dir, conn, zstd, depth, view);
        if (!_filter.Allows(ctx.Svc, ctx.Method, view.Kind)) return;
        _sink.Write(BuildLine(view, ctx));
    }

    private LineCtx BuildContext(
        string dir, string conn, bool zstd, int depth, in WireFrameView view)
    {
        ulong svc = view.ServiceUuid;
        uint method = view.MethodId;
        string? svcSource;

        if (view.Kind == WireMessageKind.Return
            && _correlator.Resolve(view.CallId, out var rsvc, out var rmethod))
        {
            svc = rsvc;
            method = rmethod;
            svcSource = "correlated";
        }
        else
        {
            svcSource = view.Kind == WireMessageKind.Return ? null : "wire";
        }

        return new LineCtx
        {
            Dir = dir, Conn = conn, Zstd = zstd, Depth = depth,
            Svc = svc, Method = method, SvcSource = svcSource
        };
    }

    private void DecodeWrapped(string dir, string conn, ReadOnlySpan<byte> span, int depth, bool zstd)
    {
        if (depth >= 4) return;
        if (!WireFrameView.TryUnwrap(span, zstd, out var nested)) return;
        int pos = 0;
        while (pos + 4 <= nested.Length)
        {
            uint size = ((uint)nested[pos] << 24) | ((uint)nested[pos + 1] << 16)
                      | ((uint)nested[pos + 2] << 8) | nested[pos + 3];
            if (size < 6 || pos + (long)size > nested.Length) break;
            DecodeFrame(dir, conn, new ReadOnlySpan<byte>(nested, pos, (int)size), depth + 1);
            pos += (int)size;
        }
    }

    private string BuildLine(in WireFrameView v, LineCtx ctx)
    {
        var node = ProtobufStructuralWalker.Walk(v.Payload.Span);
        var typed = _typed.TryDecode(ctx.Svc, ctx.Method, v.Kind, v.Payload.Span);
        var sb = new StringBuilder(256);
        AppendHeader(sb, v, ctx);
        sb.Append("\"decoded\":").Append(ProtoJson.Node(node));
        if (typed is not null) sb.Append(",\"typed\":").Append(ProtoJson.Typed(typed));
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, in WireFrameView v, in LineCtx ctx)
    {
        sb.Append('{')
          .Append("\"dir\":\"").Append(ctx.Dir).Append("\",")
          .Append("\"conn\":\"").Append(ctx.Conn).Append("\",")
          .Append("\"type\":\"").Append(v.Kind).Append("\",")
          .Append("\"zstd\":").Append(ctx.Zstd ? "true" : "false").Append(',')
          .Append("\"depth\":").Append(ctx.Depth).Append(',')
          .Append("\"svc\":").Append(ctx.Svc).Append(',')
          .Append("\"method\":").Append(ctx.Method).Append(',')
          .Append("\"svcSource\":").Append(ctx.SvcSource is null ? "null" : $"\"{ctx.SvcSource}\"").Append(',')
          .Append("\"callId\":").Append(v.CallId).Append(',')
          .Append("\"len\":").Append(v.Payload.Length).Append(',');
    }
}
