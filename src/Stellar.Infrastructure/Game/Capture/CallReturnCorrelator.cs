using System.Collections.Generic;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>
/// Bounded call_id -> {service, method} map (reference call_dict pattern), so an
/// anonymous Return can be labeled by the Call that produced it. FIFO-evicts.
/// Not thread-safe by itself — used only on the capture drain thread.
/// </summary>
internal sealed class CallReturnCorrelator
{
    private readonly int _capacity;
    private readonly Dictionary<uint, (ulong Svc, uint Method)> _map = new();
    private readonly Queue<uint> _order = new();

    public int EvictionCount { get; private set; }

    public CallReturnCorrelator(int capacity) => _capacity = capacity < 1 ? 1 : capacity;

    public void NoteCall(uint callId, ulong serviceUuid, uint methodId)
    {
        if (callId == 0) return;
        if (!_map.ContainsKey(callId)) _order.Enqueue(callId);
        _map[callId] = (serviceUuid, methodId);
        while (_map.Count > _capacity && _order.Count > 0)
        {
            var oldest = _order.Dequeue();
            if (_map.Remove(oldest)) EvictionCount++;
        }
    }

    public bool Resolve(uint callId, out ulong serviceUuid, out uint methodId)
    {
        if (_map.TryGetValue(callId, out var v)) { serviceUuid = v.Svc; methodId = v.Method; return true; }
        serviceUuid = 0; methodId = 0; return false;
    }
}
