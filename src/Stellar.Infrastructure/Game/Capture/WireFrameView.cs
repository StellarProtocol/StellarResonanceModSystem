using System;
using Stellar.Application.Abstractions;
using ZstdSharp;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>
/// Capture-local frame parser. Mirrors PandaWireTap.Parsing offsets (26/22/18).
/// </summary>
internal readonly struct WireFrameView
{
    public WireMessageKind Kind { get; init; }
    public ulong ServiceUuid { get; init; }
    public uint StubId { get; init; }
    public uint CallId { get; init; }
    public uint MethodId { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> span, ushort type, bool zstd, out WireFrameView view)
    {
        view = default;
        int off;
        WireMessageKind kind;
        ulong svc = 0;
        uint stub = 0, call = 0, method = 0;

        switch (type)
        {
            case 1:
                if (span.Length < 26) return false;
                kind = WireMessageKind.Call;
                svc = U64(span, 6); stub = U32(span, 14); call = U32(span, 18); method = U32(span, 22);
                off = 26;
                break;
            case 2:
                if (span.Length < 22) return false;
                kind = WireMessageKind.Notify;
                svc = U64(span, 6); stub = U32(span, 14); method = U32(span, 18);
                off = 22;
                break;
            case 3:
                if (span.Length < 18) return false;
                kind = WireMessageKind.Return;
                stub = U32(span, 6); call = U32(span, 10);
                off = 18;
                break;
            default:
                return false;
        }

        var payload = span.Slice(off);
        byte[]? pay = zstd ? TryUnzstd(payload) : payload.ToArray();
        if (pay is null) return false;

        view = new WireFrameView
        {
            Kind = kind, ServiceUuid = svc, StubId = stub,
            CallId = call, MethodId = method, Payload = pay
        };
        return true;
    }

    public static bool TryUnwrap(ReadOnlySpan<byte> span, bool zstd, out byte[] nested)
    {
        nested = Array.Empty<byte>();
        if (span.Length < 10) return false;
        var rest = span.Slice(10);
        if (rest.Length == 0) return true;
        if (!zstd) { nested = rest.ToArray(); return true; }
        var u = TryUnzstd(rest);
        if (u is null) return false;
        nested = u;
        return true;
    }

    internal static byte[]? TryUnzstd(ReadOnlySpan<byte> s)
    {
        try
        {
            using var d = new Decompressor();
            return d.Unwrap(s, maxDecompressedSize: 64 * 1024 * 1024).ToArray();
        }
        catch { return null; }
    }

    private static uint U32(ReadOnlySpan<byte> s, int o)
        => ((uint)s[o] << 24) | ((uint)s[o + 1] << 16) | ((uint)s[o + 2] << 8) | s[o + 3];

    private static ulong U64(ReadOnlySpan<byte> s, int o)
    {
        ulong r = 0;
        for (int i = 0; i < 8; i++) r = (r << 8) | s[o + i];
        return r;
    }
}
