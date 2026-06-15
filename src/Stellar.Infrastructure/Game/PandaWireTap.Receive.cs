using System;
using System.Collections.Concurrent;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// HarmonyX prefix hooks + per-connection reassembly drain for
/// <see cref="PandaWireTap"/>. Runs on the network I/O thread at hundreds of
/// packets/sec; must be bounded and never throw across the IL2CPP boundary.
/// </summary>
internal sealed partial class PandaWireTap
{
    // Per-ZTcpClient reassembly buffer. ZTcpConnection.OnData fires per recv()
    // chunk, NOT per logical packet — large packets (zstd-compressed history
    // replies in particular) arrive across multiple chunks. Append every chunk
    // to a per-connection buffer and drain complete packets from the front.
    // Key = ZTcpClient instance reference (passed as __args[1] to OnData and
    // stashed in _currentTcpClient before HandleWireChunk runs).
    private readonly ConcurrentDictionary<object, ReassemblyBuffer> _reassemblyByClient = new();

    // Hard cap on reassembly buffer size per client. 16 MB is well above any
    // expected single-packet size (chat history is ~25 KB plain, ~5 KB zstd) and
    // far below memory pressure thresholds. If a client's buffer exceeds this we
    // assume desync (corrupted size header, mid-stream disconnect) and reset.
    private const int MaxReassemblyBufferBytes = 16 * 1024 * 1024;
    private const int MaxLogicalPacketBytes    = 16 * 1024 * 1024;

    // Thread-static current-call ZTcpClient. OnTcpDataReceivedPrefix runs on
    // the I/O thread and writes this BEFORE HandleWireChunk runs, so the
    // dispatcher can attach the per-connection client to each WireEnvelope.
    [ThreadStatic]
    private static object? _currentTcpClient;

    // IL2CPP ReadOnlySpan<byte> extractor lives in Il2CppSpanCoercion (shared
    // with PandaChatProbe + PandaCombatStubProbe).
    private bool _onDataExtractionFailLogged;

    // First-time diagnostic flags. Plain bools — single-threaded on the I/O
    // path, so no interlock needed.
    private bool _firstFrameDispatchedLogged;
    private bool _nestedFrameDesyncLogged;

    /// <summary>
    /// HarmonyX PREFIX on <c>ZCode.ZNet.ZTcpConnection.OnData(ReadOnlySpan&lt;byte&gt;, ZTcpClient)</c>.
    /// Runs on the network I/O thread at hundreds of packets/sec. Bounded
    /// per-packet work — coerce the span to a managed byte[], stash the
    /// ZTcpClient for downstream dispatch, hand off to <see cref="HandleWireChunk"/>.
    /// Never throws across the IL2CPP boundary.
    /// </summary>
    private static void OnTcpDataReceivedPrefix(object?[] __args)
    {
        var tap = Instance;
        if (tap is null) return;

        // Perf harness: time per-packet wire parsing (net I/O thread; scales with
        // login-connection traffic = world chat in a crowd). No-op off.
        var _perfT = Stellar.Abstractions.Diagnostics.PerfProbe.HookBegin();
        var _perfA = Stellar.Abstractions.Diagnostics.PerfProbe.HookBeginAlloc();
        try
        {
            if (__args is null || __args.Length < 1) return;

            // Stash __args[1] (ZTcpClient instance) so the dispatcher can
            // attach it to each WireEnvelope. Cleared in finally.
            if (__args.Length >= 2)
            {
                _currentTcpClient = __args[1];
            }

            var arg0 = __args[0];
            if (arg0 is null) return;

            var bytes = tap.CoerceReceivedBytes(arg0, hookName: "OnData");
            if (bytes is null || bytes.Length < 1) return;
            tap.HandleWireChunk(bytes);
        }
        catch (Exception ex)
        {
            // Postfix runs on the network I/O thread. Never throw across the
            // IL2CPP boundary. Swallow with a single warning.
            Instance?._log.Warning($"[WireTap] OnData prefix threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Don't let the thread-static keep the ZTcpClient alive across
            // unrelated I/O thread work.
            _currentTcpClient = null;
            Stellar.Abstractions.Diagnostics.PerfProbe.HookEndWire(_perfT, _perfA);
        }
    }

    /// <summary>
    /// HarmonyX PREFIX on <c>ZCode.ZNet.ZUdpConnection.ProcessKcpData(ReadOnlySpan&lt;byte&gt;)</c>.
    /// Mirrors <see cref="OnTcpDataReceivedPrefix"/> — same byte extraction
    /// path (ReadOnlySpan&lt;byte&gt; ToArray), same downstream reassembly +
    /// dispatch pipeline. The signature differs from TCP: ProcessKcpData is
    /// a one-arg instance method, so the connection identity comes from
    /// <c>__instance</c> (the ZUdpConnection), not from a second positional
    /// argument. The reassembly map is keyed by <c>object</c> reference, so
    /// the ZUdpConnection works as a unique reassembly key the same way the
    /// ZTcpClient does on the TCP path.
    ///
    /// ProcessKcpData fires AFTER KCP reassembles a complete application-layer
    /// payload, so the bytes here are zproto frames — the same shape the
    /// wire-frame parser already understands.
    ///
    /// Runs on the network I/O thread. Never throws across the IL2CPP boundary.
    /// </summary>
    private static void OnUdpDataReceivedPrefix(object __instance, object?[] __args)
    {
        var tap = Instance;
        if (tap is null) return;

        var _perfT = Stellar.Abstractions.Diagnostics.PerfProbe.HookBegin();
        var _perfA = Stellar.Abstractions.Diagnostics.PerfProbe.HookBeginAlloc();
        try
        {
            if (__args is null || __args.Length < 1) return;

            // Use the ZUdpConnection itself as the connection identity for
            // this packet. The reassembly map is keyed by object reference;
            // HandleWireChunk reads _currentTcpClient and treats it as opaque.
            _currentTcpClient = __instance;

            var arg0 = __args[0];
            if (arg0 is null) return;

            var bytes = tap.CoerceReceivedBytes(arg0, hookName: "ProcessKcpData");
            if (bytes is null || bytes.Length < 1) return;
            tap.HandleWireChunk(bytes);
        }
        catch (Exception ex)
        {
            Instance?._log.Warning($"[WireTap] ProcessKcpData prefix threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _currentTcpClient = null;
            Stellar.Abstractions.Diagnostics.PerfProbe.HookEndWire(_perfT, _perfA);
        }
    }

    /// <summary>
    /// Coerce the first prefix-argument (either a managed <c>byte[]</c> or an
    /// IL2CPP-projected <c>ReadOnlySpan&lt;byte&gt;</c> wrapper) into a managed
    /// <c>byte[]</c>. Shared by the TCP and UDP prefixes because both call
    /// sites box the same IL2CPP wrapper. Returns null on coercion failure
    /// (logged once per hook).
    ///
    /// The IL2CPP ReadOnlySpan&lt;byte&gt; indexer (get_Item) is broken under
    /// HarmonyX ref-struct boxing — every read returns the 0x40 buffer
    /// sentinel regardless of actual content. ToArray() on the same wrapper
    /// dereferences the byref inside the method and returns the real recv'd
    /// bytes.
    /// </summary>
    private byte[]? CoerceReceivedBytes(object arg0, string hookName)
    {
        if (arg0 is byte[] arr) return arr;

        // First-packet resolve (one-shot). Shared with the chat/combat probes
        // because every IL2CPP site boxes the same ReadOnlySpan<byte> wrapper.
        if (!Il2CppSpanCoercion.SpanExtractorReady && System.Threading.Interlocked.Exchange(ref Il2CppSpanCoercion.SpanExtractorResolved, 1) == 0)
        {
            Il2CppSpanCoercion.ResolveSpanExtractor(_log, arg0.GetType());
        }

        var toArr = Il2CppSpanCoercion.SpanToArrayMethod;
        if (toArr is null)
        {
            if (!_onDataExtractionFailLogged)
            {
                _onDataExtractionFailLogged = true;
                _log.Warning($"[WireTap] {hookName} no extractor available for arg0 type: {arg0.GetType().FullName}");
            }
            return null;
        }

        object? rawToArr;
        try { rawToArr = toArr.Invoke(arg0, null); }
        catch { return null; }
        if (rawToArr is null) return null;

        return Il2CppSpanCoercion.CoerceToByteArray(rawToArr);
    }

    /// <summary>
    /// Append a recv() chunk to the per-client reassembly buffer and drain
    /// any complete logical packets from the front. Matches BPSR-B's
    /// <c>_packet_process_loop</c>: ZTcpConnection.OnData fires per recv
    /// chunk, NOT per logical packet — large packets span multiple chunks.
    /// Runs on the network I/O thread; single-threaded per ZTcpClient.
    /// </summary>
    private void HandleWireChunk(byte[] chunk)
    {
        var client = _currentTcpClient;
        if (client is null)
        {
            // Treat the chunk as a self-contained packet — same behaviour as
            // before reassembly existed. Only triggers if OnTcpDataReceived
            // failed to populate _currentTcpClient, which shouldn't happen.
            HandleWireBytes(chunk, connection: null);
            return;
        }

        var rb = _reassemblyByClient.GetOrAdd(client, _ => new ReassemblyBuffer());

        // Lock per-buffer. ZTcpConnection.OnData is normally single-threaded
        // per connection, but defensive locking eliminates the risk of
        // corruption if the game ever dispatches recvs across threads.
        lock (rb)
        {
            rb.Append(chunk);

            if (rb.Length > MaxReassemblyBufferBytes)
            {
                _log.Warning($"[WireTap] reassembly buffer for {client.GetType().Name} exceeded {MaxReassemblyBufferBytes} bytes; resetting");
                rb.Length = 0;
                return;
            }

            DrainReassembledFrames(rb, client);
        }
    }

    /// <summary>
    /// Inner drain loop for <see cref="HandleWireChunk"/>. Called with the
    /// reassembly buffer's monitor held. Pulls complete logical packets off
    /// the front of <paramref name="rb"/> and dispatches each through
    /// <see cref="HandleWireBytes"/>. Resets the buffer on size-header
    /// desync.
    /// </summary>
    private void DrainReassembledFrames(ReassemblyBuffer rb, object client)
    {
        while (rb.Length >= 4)
        {
            uint size =
                ((uint)rb.Data[0] << 24) | ((uint)rb.Data[1] << 16)
                | ((uint)rb.Data[2] << 8) | rb.Data[3];

            // Size sanity. TCP delivers in order, so the start of the
            // buffer MUST be the start of a logical packet. If size is
            // outside the sane range, the buffer is desynced — clear it.
            if (size < 6 || size > MaxLogicalPacketBytes)
            {
                var head = rb.Length >= 16 ? 16 : rb.Length;
                var sb = new System.Text.StringBuilder(head * 3);
                for (int i = 0; i < head; i++) { sb.Append(rb.Data[i].ToString("X2")); sb.Append(' '); }
                _log.Warning($"[WireTap] reassembly desync — size header={size} bufferLen={rb.Length}; clearing. first {head}B: {sb}");
                rb.Length = 0;
                return;
            }

            if (rb.Length < size) break; // incomplete — wait for more chunks

            var packet = new byte[(int)size];
            System.Buffer.BlockCopy(rb.Data, 0, packet, 0, (int)size);
            rb.Drop((int)size);

            HandleWireBytes(packet, client);
        }
    }

    /// <summary>
    /// Per-complete-packet dispatcher entry point (depth 0).
    /// Records the raw top-level frame into the capture (if enabled) BEFORE
    /// processing so wrappers are captured once; the capture does its own nested
    /// expansion. Delegates to the depth-aware overload.
    /// </summary>
    private void HandleWireBytes(ReadOnlySpan<byte> span, object? connection)
    {
        // Guard form avoids ToArray() allocation when capture is disabled.
        if (_capture is not null) _capture.Record("in", span.ToArray(), connection);
        HandleWireBytes(span, connection, depth: 0);
    }

    /// <summary>
    /// Per-complete-packet dispatcher. Reads the wire header, decompresses
    /// the payload if zstd-framed, builds a <see cref="WireEnvelope"/>, and
    /// hands it to <see cref="Dispatch"/>. For FrameUp/FrameDown, recursively
    /// unwraps the nested frames first (bounded by <c>MaxFrameUnwrapDepth</c>).
    ///
    /// Packet shape (big-endian, per BPSR-B's bpsr_client/packet.py):
    /// <code>
    ///   Notify/Call: [size:4][flags:2][service_uuid:8][stub_id:4][call_id:4][method_id:4][payload]
    ///   Return:      [size:4][flags:2][stub_id:4][call_id:4][error_id:4][payload]
    ///   FrameDown/Up:[size:4][flags:2][sequence:4][nested_frames…]
    /// </code>
    /// flags low 15 bits = ZprotoMsgTypeId; flags high bit (0x8000) = zstd.
    /// </summary>
    private void HandleWireBytes(ReadOnlySpan<byte> span, object? connection, int depth)
    {
        if (span.Length < 6) return;

        ushort flags = (ushort)((span[4] << 8) | span[5]);
        ushort msgTypeRaw = (ushort)(flags & 0x7FFF);
        bool isZstdCompressed = (flags & 0x8000) != 0;

        if (msgTypeRaw == MsgTypeFrameUp || msgTypeRaw == MsgTypeFrameDown)
        {
            UnwrapAndProcess(span, connection, depth, isZstdCompressed);
            return;
        }

        if (span.Length < 14) return;
        if (!TryParseWireHeader(span, msgTypeRaw, out var header, out var payloadOffset)) return;
        if (!TryPreparePayload(span, payloadOffset, isZstdCompressed, header, out var payload)) return;

        var env = new WireEnvelope
        {
            Kind        = header.Kind,
            ServiceUuid = header.ServiceUuid,
            StubId      = header.StubId,
            CallId      = header.CallId,
            MethodId    = header.MethodId,
            ErrorCode   = header.ErrorCode,
            Payload     = payload,
            Connection  = connection,
        };

        if (!_firstFrameDispatchedLogged)
        {
            _firstFrameDispatchedLogged = true;
            _log.Info($"[WireTap] first frame dispatched: kind={header.Kind} svc={header.ServiceUuid} method={header.MethodId} callId={header.CallId} payloadLen={payload.Length} zstd={isZstdCompressed}");
        }

        Dispatch(env);
    }

    private void UnwrapAndProcess(ReadOnlySpan<byte> span, object? connection, int depth, bool isZstd)
    {
        if (depth >= MaxFrameUnwrapDepth)
        {
            if (!_frameUnwrapDepthLogged)
            {
                _frameUnwrapDepthLogged = true;
                _log.Warning($"[WireTap] frame unwrap depth {MaxFrameUnwrapDepth} exceeded; nested frame skipped");
            }
            return;
        }
        if (!TryUnwrapNested(span, isZstd, out var nested)) return;
        ProcessFrameBuffer(nested, connection, depth + 1);
    }

    /// <summary>Iterate length-prefixed frames in a buffer and process each.</summary>
    private void ProcessFrameBuffer(byte[] buffer, object? connection, int depth)
    {
        int pos = 0;
        while (pos + 4 <= buffer.Length)
        {
            uint size = ((uint)buffer[pos] << 24) | ((uint)buffer[pos + 1] << 16)
                      | ((uint)buffer[pos + 2] << 8) | buffer[pos + 3];
            if (size < 6 || pos + (long)size > buffer.Length)
            {
                if (!_nestedFrameDesyncLogged)
                {
                    _nestedFrameDesyncLogged = true;
                    _log.Warning($"[WireTap] nested frame buffer desync at pos={pos} size={size} bufferLen={buffer.Length}; remaining nested frames skipped");
                }
                break;
            }
            HandleWireBytes(new ReadOnlySpan<byte>(buffer, pos, (int)size), connection, depth);
            pos += (int)size;
        }
    }
}
