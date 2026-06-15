using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Live equipped-set maintenance from WorldNtf <c>SyncContainerDirtyData</c>
/// (method 22) incremental deltas for <see cref="PandaInventoryWireCapture"/>.
///
/// <para>A manual in-game equip arrives as a method-22 dirty delta, not a full
/// method-21 sync. The proto is
/// <c>SyncContainerDirtyData { BufferStream VData = 1 }</c> and
/// <c>BufferStream { bytes Buffer = 1 }</c>, so the tight delta bytes are just
/// the nested <c>field 1 → field 1</c> of the raw stub payload's protobuf wire.
/// We extract them directly from the wire — exactly the bytes BPSR-B's reader
/// consumes — and deliberately do NOT read the game's in-memory
/// <c>BufferStream.Buffer</c> object, whose accessor returns a
/// 0xDEADBEEF-guarded backing array rather than the wire content. The tight
/// bytes go to the pure <see cref="ContainerDirtyDeltaReader"/>, and the slot
/// adds/removes are applied to the shared equipped snapshot
/// (<see cref="InventoryProbeState.EquippedSnapshot"/>) copy-on-write.</para>
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    private bool _dirtyExtractFailLogged;

    // Entry from HandleStubCall on a WorldNtf method-22 dirty delta. Extracts the
    // tight delta bytes from the wire, parses the mod_slots delta, applies it.
    private void HandleDirtyContainer(object stubCall, Type stubType)
    {
        var delta = ExtractDirtyDeltaBytes(stubCall, stubType);
        if (delta is null || delta.Length == 0) return;

        var slotDelta = ContainerDirtyDeltaReader.Read(delta);
        if (!slotDelta.Touched) return;

        ApplyModSlotDelta(slotDelta);
    }

    // Applies a parsed mod_slots delta to the shared equipped snapshot
    // (_state.EquippedSnapshot) copy-on-write: clone
    // the current snapshot (or start empty if no full sync has seeded one yet),
    // apply adds/updates then removes, and publish the new dict atomically. The
    // snapshot is tiny (<= ~12 slots) so the clone is cheap.
    private void ApplyModSlotDelta(ModSlotDelta slotDelta)
    {
        var current = _state.EquippedSnapshot;
        var next = current is null
            ? new Dictionary<int, long>(slotDelta.AddsAndUpdates.Count)
            : new Dictionary<int, long>(current);

        foreach (var kv in slotDelta.AddsAndUpdates)
        {
            if (kv.Key > 0) next[kv.Key] = kv.Value;
        }
        foreach (var slot in slotDelta.Removes)
        {
            next.Remove(slot);
        }

        _state.PublishEquippedSnapshot(next);
        ContainerDirtyDeltaReader.DiagEquippedSetSize(next.Count);
    }

    // Tight delta bytes = SyncContainerDirtyData.VData(1).Buffer(1), walked
    // straight out of the raw stub payload's protobuf wire (NOT the game's
    // guarded in-memory BufferStream). Returns null on any malformed input.
    private byte[]? ExtractDirtyDeltaBytes(object stubCall, Type stubType)
    {
        var wire = ExtractStubPayloadBytes(stubCall, stubType);
        if (wire is null || wire.Length == 0) return null;

        var vData = ReadLenDelimitedField(wire, fieldNum: 1);   // SyncContainerDirtyData.VData
        if (vData is null) { LogDirtyExtractFail("VData (field 1) not found"); return null; }

        var buffer = ReadLenDelimitedField(vData, fieldNum: 1);  // BufferStream.Buffer
        if (buffer is null) { LogDirtyExtractFail("Buffer (field 1) not found in VData"); return null; }

        return buffer;
    }

    // Minimal protobuf scan: returns a fresh byte[] of the first length-delimited
    // (wire-type 2) field matching fieldNum, skipping other fields/wire types.
    private static byte[]? ReadLenDelimitedField(byte[] buf, int fieldNum)
    {
        var pos = 0;
        var end = buf.Length;
        while (pos < end)
        {
            if (!TryReadVarint(buf, ref pos, end, out var tag)) return null;
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(buf, ref pos, end, out _)) return null;
                    break;
                case 1: // 64-bit
                    pos += 8;
                    break;
                case 5: // 32-bit
                    pos += 4;
                    break;
                case 2: // length-delimited
                    if (!TryReadVarint(buf, ref pos, end, out var len)) return null;
                    var dataEnd = pos + (int)len;
                    if (dataEnd < pos || dataEnd > end) return null;
                    if (field == fieldNum)
                    {
                        var outBuf = new byte[(int)len];
                        Array.Copy(buf, pos, outBuf, 0, (int)len);
                        return outBuf;
                    }
                    pos = dataEnd;
                    break;
                default:
                    return null; // groups / unknown — bail
            }
        }
        return null;
    }

    private static bool TryReadVarint(byte[] buf, ref int pos, int end, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (pos < end && shift < 64)
        {
            var b = buf[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private void LogDirtyExtractFail(string detail)
    {
        if (_dirtyExtractFailLogged) return;
        _dirtyExtractFailLogged = true;
        _log.Warning($"[Inventory] dirty-delta wire extract failed: {detail}");
    }
}
