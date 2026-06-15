using System;

namespace Stellar.Application.Abstractions;

internal enum WireMessageKind
{
    Call,
    Notify,
    Return,
    Echo,
}

internal readonly struct WireEnvelope
{
    public WireMessageKind Kind { get; init; }
    public ulong   ServiceUuid { get; init; }  // 0 for Return / Echo
    public uint    StubId      { get; init; }
    public uint    CallId      { get; init; }  // 0 for Notify / Echo
    public uint    MethodId    { get; init; }  // 0 for Return / Echo
    public uint    ErrorCode   { get; init; }  // Return only
    public ReadOnlyMemory<byte> Payload { get; init; }  // zstd already decompressed

    /// <summary>
    /// Opaque per-connection identity (e.g. the IL2CPP ZTcpClient reference)
    /// kept as <c>object</c> so Application stays free of game/IL2CPP types.
    /// Infrastructure adapters can downcast; consumers that don't care leave
    /// it alone.
    /// </summary>
    public object? Connection { get; init; }
}

/// <summary>
/// Outbound port: Infrastructure-side wire-tap that maintains the single
/// TCP recv hook and dispatches parsed envelopes to registered handlers
/// keyed by (serviceUuid, methodId). Handlers fire on the wire thread.
/// </summary>
internal interface IWireTap
{
    void Register(ulong serviceUuid, uint methodId, Action<WireEnvelope> handler);

    /// <summary>
    /// Register a handler that receives every <c>Return</c> envelope. Returns
    /// don't carry service_uuid/method_id on the wire (server-side correlation
    /// only) — consumers correlate via <see cref="WireEnvelope.CallId"/>
    /// against their own pending-call tables.
    /// </summary>
    void RegisterReturn(Action<WireEnvelope> handler);
}
