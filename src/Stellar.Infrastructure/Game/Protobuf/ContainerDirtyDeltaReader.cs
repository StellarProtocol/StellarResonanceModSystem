using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Outcome of parsing a WorldNtf <c>SyncContainerDirtyData</c> (method 22)
/// delta for the equipped-module set. <see cref="Touched"/> is true when the
/// delta carried a <c>Mod.mod_slots</c> (field 57 → field 1) entry — only then
/// should the caller mutate its equipped-set snapshot.
/// </summary>
internal readonly record struct ModSlotDelta(
    bool Touched,
    IReadOnlyDictionary<int, long> AddsAndUpdates,
    IReadOnlyList<int> Removes)
{
    internal static ModSlotDelta None { get; } =
        new(false, new Dictionary<int, long>(0), Array.Empty<int>());
}

/// <summary>
/// Pure (IL2CPP-free, allocation-light) parser for the game's custom binary
/// container-delta format carried by WorldNtf <c>SyncContainerDirtyData</c>
/// (method 22). Mirrors the proven BPSR-B <c>BlobReader</c>
/// (<c>world_notify_sync_container_dirty_data.py</c>) — same little-endian
/// layout, same BEGIN/END/skip sentinels — but targets the
/// <c>CharSerialize.mod</c> (field 57) → <c>Mod.mod_slots</c> (field 1)
/// path instead of the currency map.
///
/// <para><b>Binary format (little-endian i32/i64):</b></para>
/// <list type="bullet">
///   <item>Container = <c>tag</c> (i32, BEGIN = -2), <c>size</c> (i32), then
///         field entries until <c>tag</c> (i32, END = -3). <c>size == END</c>
///         denotes an empty container.</item>
///   <item>Field entry = <c>index</c> (i32, &gt; 0 = a proto field number),
///         then field-specific data.</item>
///   <item>Nested MESSAGE field (e.g. <c>mod</c>, field 57) = its own
///         BEGIN/size/inner-fields/END container — descend into it.</item>
///   <item>MAP field data = <c>addCount</c> (i32). Sentinels: <c>-4</c> = skip
///         (no change to this map), <c>-1</c> = add-only (re-read the real
///         addCount next). Otherwise <c>addCount</c>, then <c>removeCount</c>
///         (i32), then <c>updateCount</c> (i32), then that many add / remove /
///         update entries.</item>
/// </list>
///
/// <para><b>Scalar-valued map (<c>mod_slots</c>, <c>map&lt;int32, int64&gt;</c>):</b>
/// because the value is a scalar (not a nested message like the currency map),
/// an add/update entry is <c>key</c> (i32) + <c>value</c> (i64 read directly),
/// and a remove entry is <c>key</c> (i32). BPSR-B only demonstrates the
/// message-valued currency map, so the scalar case here is verified against the
/// live wire via the diagnostic dump in the sibling
/// <c>ContainerDirtyDeltaReader.Diagnostics.cs</c>.</para>
///
/// <para>Never throws: every malformed-input path returns
/// <see cref="ModSlotDelta.None"/> after logging the offset + surrounding i32s
/// through the diagnostic sink, so a bad delta on the network thread can never
/// crash the game or corrupt the caller's snapshot.</para>
/// </summary>
internal static partial class ContainerDirtyDeltaReader
{
    private const int FieldMod = 57;        // CharSerialize.mod
    private const int FieldModSlots = 1;    // Mod.mod_slots (map<int32,int64>)

    private const int TagBegin = -2;
    private const int TagEnd = -3;
    private const int MapSkip = -4;
    private const int MapAddOnly = -1;

    /// <summary>
    /// Parses the CharSerialize-level delta <paramref name="buffer"/> and, if it
    /// touches <c>Mod.mod_slots</c>, returns the slot adds/updates and removes.
    /// Returns <see cref="ModSlotDelta.None"/> (Touched = false) when the delta
    /// carries no mod-slot change or when the buffer is malformed.
    /// </summary>
    public static ModSlotDelta Read(byte[]? buffer)
    {
        if (buffer is null || buffer.Length < 8)
        {
            return ModSlotDelta.None;
        }

        DiagBufferLength(buffer.Length);

        var reader = new BlobReader(buffer);
        if (!TryEnterContainer(ref reader, "CharSerialize"))
        {
            return ModSlotDelta.None;
        }

        return WalkCharSerializeFields(ref reader);
    }

    // Walks the top-level CharSerialize field entries, descending into field 57
    // (Mod) when found and returning its mod_slots delta. Every other field is
    // skipped via its BEGIN/size container. Returns None if mod_slots is absent.
    private static ModSlotDelta WalkCharSerializeFields(ref BlobReader reader)
    {
        var index = reader.ReadInt32();
        while (index > 0)
        {
            DiagTopField(index, reader.Offset);
            if (index == FieldMod)
            {
                return ReadModContainer(ref reader);
            }

            if (!TrySkipUnknownField(ref reader))
            {
                DiagParseFailure("CharSerialize.skip", ref reader);
                return ModSlotDelta.None;
            }

            if (reader.Remaining < 4)
            {
                break;
            }
            index = reader.ReadInt32();
        }
        return ModSlotDelta.None;
    }

    // Descends the Mod (field 57) nested container and reads its mod_slots
    // (field 1) map delta. Other inner fields (e.g. mod_infos = 2) are skipped.
    private static ModSlotDelta ReadModContainer(ref BlobReader reader)
    {
        if (!TryEnterContainer(ref reader, "Mod"))
        {
            return ModSlotDelta.None;
        }

        var index = reader.ReadInt32();
        while (index > 0)
        {
            DiagModField(index, reader.Offset);
            if (index == FieldModSlots)
            {
                return ReadModSlotsMap(ref reader);
            }

            if (!TrySkipUnknownField(ref reader))
            {
                DiagParseFailure("Mod.skip", ref reader);
                return ModSlotDelta.None;
            }

            if (reader.Remaining < 4)
            {
                break;
            }
            index = reader.ReadInt32();
        }
        return ModSlotDelta.None;
    }

    // Reads the mod_slots map delta. Scalar-valued map<int32,int64>: an
    // add/update entry is key(i32)+value(i64); a remove entry is key(i32).
    private static ModSlotDelta ReadModSlotsMap(ref BlobReader reader)
    {
        if (reader.Remaining < 4)
        {
            return ModSlotDelta.None;
        }

        var addCount = reader.ReadInt32();
        if (addCount == MapSkip)
        {
            DiagModSlotsCounts(0, 0, 0, skip: true);
            return ModSlotDelta.None;
        }

        var removeCount = 0;
        var updateCount = 0;
        if (addCount == MapAddOnly)
        {
            addCount = reader.ReadInt32();
        }
        else
        {
            removeCount = reader.ReadInt32();
            updateCount = reader.ReadInt32();
        }

        DiagModSlotsCounts(addCount, removeCount, updateCount, skip: false);

        if (!CountsAreSane(addCount, removeCount, updateCount, ref reader))
        {
            DiagParseFailure("mod_slots.counts", ref reader);
            return ModSlotDelta.None;
        }

        return ApplyModSlotEntries(ref reader, addCount, removeCount, updateCount);
    }

    // Reads addCount + updateCount key/value pairs and removeCount keys. Adds and
    // updates both write to the snapshot, so they share the same dictionary.
    private static ModSlotDelta ApplyModSlotEntries(
        ref BlobReader reader, int addCount, int removeCount, int updateCount)
    {
        var addsAndUpdates = new Dictionary<int, long>(addCount + updateCount);
        var removes = new List<int>(removeCount);

        for (var i = 0; i < addCount; i++)
        {
            if (!TryReadSlotEntry(ref reader, addsAndUpdates, "add")) return ModSlotDelta.None;
        }
        for (var i = 0; i < removeCount; i++)
        {
            if (reader.Remaining < 4) { DiagParseFailure("mod_slots.remove", ref reader); return ModSlotDelta.None; }
            var slot = reader.ReadInt32();
            removes.Add(slot);
            DiagModSlotRemove(slot);
        }
        for (var i = 0; i < updateCount; i++)
        {
            if (!TryReadSlotEntry(ref reader, addsAndUpdates, "update")) return ModSlotDelta.None;
        }

        return new ModSlotDelta(true, addsAndUpdates, removes);
    }

    private static bool TryReadSlotEntry(
        ref BlobReader reader, Dictionary<int, long> sink, string kind)
    {
        if (reader.Remaining < 12)
        {
            DiagParseFailure($"mod_slots.{kind}", ref reader);
            return false;
        }
        var slot = reader.ReadInt32();
        var uuid = reader.ReadInt64();
        sink[slot] = uuid;
        DiagModSlotEntry(kind, slot, uuid);
        return true;
    }

    // Reads BEGIN + size and confirms BEGIN. size is consumed but the caller
    // walks inner fields directly (it does not jump). Returns false on a missing
    // or empty container so the caller bails cleanly.
    private static bool TryEnterContainer(ref BlobReader reader, string label)
    {
        if (reader.Remaining < 8)
        {
            DiagParseFailure($"{label}.enter", ref reader);
            return false;
        }
        var tag = reader.ReadInt32();
        if (tag != TagBegin)
        {
            DiagBadBeginTag(label, tag);
            return false;
        }
        var size = reader.ReadInt32();
        // size == END marks an empty container — nothing to walk.
        return size != TagEnd;
    }

    // Skips an unknown container field: read BEGIN + size, then jump `size`
    // bytes. Mirrors BPSR-B's _skip_unknown_field. Returns false on a malformed
    // or out-of-range container.
    private static bool TrySkipUnknownField(ref BlobReader reader)
    {
        if (reader.Remaining < 8) return false;
        var tag = reader.ReadInt32();
        if (tag != TagBegin) return false;
        var size = reader.ReadInt32();
        if (size == TagEnd) return true;   // empty container
        if (size < 0 || size > reader.Remaining) return false;
        reader.Skip(size);
        return true;
    }

    // Bounds sanity: each add/update entry is 12 bytes (i32 key + i64 value) and
    // each remove entry is 4 bytes (i32 key). Reject obviously-bad counts before
    // allocating so a corrupt header can't drive a huge allocation or OOR read.
    private static bool CountsAreSane(int add, int remove, int update, ref BlobReader reader)
    {
        if (add < 0 || remove < 0 || update < 0) return false;
        long needed = (long)add * 12 + (long)remove * 4 + (long)update * 12;
        return needed <= reader.Remaining;
    }
}
