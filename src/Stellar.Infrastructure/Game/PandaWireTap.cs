using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Shared wire-tap. Single owner of the TCP recv HarmonyX hook
/// (<c>ZCode.ZNet.ZTcpConnection.OnData</c>); parses frames; dispatches parsed
/// envelopes to consumers registered by <c>(serviceUuid, methodId)</c>.
/// Returns dispatch via <see cref="RegisterReturn"/> because Returns don't
/// carry service_uuid/method_id on the wire — consumers correlate via call_id.
///
/// All handlers fire on the network I/O thread. Handler implementations MUST
/// be brief and non-blocking; queue work to other threads if needed.
///
/// The implementation is split across partials:
///   * PandaWireTap.cs            — dispatch table + Register/Dispatch (this file)
///   * PandaWireTap.Patching.cs   — HarmonyX installation (TCP + UDP)
///   * PandaWireTap.Receive.cs    — prefix hooks + reassembly drain
///   * PandaWireTap.Parsing.cs    — wire-header decode + payload prep
///   * PandaWireTap.Diagnostics.cs — opt-in first-tuple logging
/// </summary>
internal sealed partial class PandaWireTap : IWireTap
{
    // -------- Dispatch table ------------------------------------------------

    private readonly IPluginLog _log;

    // Copy-on-write dispatch table. Register allocates a new array under the
    // lock; Dispatch reads the array reference without locking. This avoids
    // the per-packet List<T> snapshot allocation that the previous design
    // performed on the network I/O thread (hundreds of packets/sec). The
    // CombatService event handler array (see CombatService.cs) uses the same
    // pattern; this brings the wire tap in line.
    private readonly Dictionary<(ulong ServiceUuid, uint MethodId), Action<WireEnvelope>[]> _handlers = new();
    private Action<WireEnvelope>[] _returnHandlers = Array.Empty<Action<WireEnvelope>>();
    private readonly object _lock = new();

    public PandaWireTap(IPluginLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Register(ulong serviceUuid, uint methodId, Action<WireEnvelope> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_lock)
        {
            var key = (serviceUuid, methodId);
            if (!_handlers.TryGetValue(key, out var existing))
            {
                _handlers[key] = new[] { handler };
                return;
            }
            var next = new Action<WireEnvelope>[existing.Length + 1];
            Array.Copy(existing, next, existing.Length);
            next[existing.Length] = handler;
            // The dictionary entry is written under the lock, so readers that
            // already hold a stale reference simply iterate the prior array
            // — safe, since arrays are immutable once published.
            _handlers[key] = next;
        }
    }

    public void RegisterReturn(Action<WireEnvelope> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        lock (_lock)
        {
            var existing = _returnHandlers;
            var next = new Action<WireEnvelope>[existing.Length + 1];
            Array.Copy(existing, next, existing.Length);
            next[existing.Length] = handler;
            // Volatile publish so readers on other threads see the new array.
            Volatile.Write(ref _returnHandlers, next);
        }
    }

    private Capture.WirePacketCapture? _capture;

    private void StartCaptureIfEnabled()
    {
        var spec = System.Environment.GetEnvironmentVariable("STELLAR_WIRECAP");
        var filter = Capture.CaptureFilter.Parse(spec);
        if (!filter.Enabled)
        {
            if (filter.Error is not null)
                _log.Warning($"[WireCap] disabled — bad STELLAR_WIRECAP: {filter.Error}");
            return;
        }
        var dir = System.AppDomain.CurrentDomain.BaseDirectory;
        var path = System.IO.Path.Combine(dir, $"stellar-wirecap-{System.DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _capture = new Capture.WirePacketCapture(
            filter, new Capture.JsonlCaptureWriter(path, maxLines: 500_000));
        _capture.Start();
        _log.Info($"[WireCap] ENABLED spec='{spec}' → {path}");
    }

    public void DisposeCapture() => _capture?.Dispose();

    // Internal test hooks.
    internal void DispatchForTest(WireEnvelope envelope) => Dispatch(envelope);
    internal void HandleWireBytesForTest(byte[] frame) => HandleWireBytes(frame, connection: null, depth: 0);

    internal void Dispatch(WireEnvelope envelope)
    {
        Action<WireEnvelope>[]? snapshot;
        if (envelope.Kind == WireMessageKind.Return)
        {
            snapshot = Volatile.Read(ref _returnHandlers);
            if (snapshot.Length == 0) return;
        }
        else
        {
            // Dictionary reads without a lock are NOT safe across writers, so
            // take the lock just long enough to copy out the array reference.
            // Iteration happens outside the lock — the array is immutable.
            lock (_lock)
            {
                if (!_handlers.TryGetValue((envelope.ServiceUuid, envelope.MethodId), out snapshot))
                    return;
            }
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i](envelope); }
            catch (Exception ex)
            {
                _log.Warning($"[WireTap] handler threw for kind={envelope.Kind} ({envelope.ServiceUuid},{envelope.MethodId}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
