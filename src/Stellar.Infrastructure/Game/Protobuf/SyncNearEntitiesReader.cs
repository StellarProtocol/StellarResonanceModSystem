using System;
using System.Collections.Generic;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// One <c>appear</c> entity entry surfaced by
/// <see cref="SyncNearEntitiesReader.TryReadAppearAndDisappear"/>. Only the
/// fields the combat probe actually consumes are projected — currently the
/// uuid (field 1) and, when present, the <c>AttrCollection</c> sub-message
/// (field 3) used to extract <c>AttrName</c>. Everything else on the wire
/// Entity (ent_type, temp_attrs, body_part_infos, passive_skill_infos, buffs,
/// appear_type, magnetic queue) is skipped — adding more is a trivial
/// extension when a consumer needs it.
/// </summary>
internal readonly record struct AppearEntityMsg(long Uuid, AttrCollectionMsg? Attrs);

/// <summary>
/// Pure parser for <c>SyncNearEntities</c> — the message that tells the client
/// which entities entered or left its AOI.
///
/// Schema:
/// <code>
///   message SyncNearEntities {
///     repeated Entity          appear    = 1;
///     repeated DisappearEntity disappear = 2;
///   }
///   message Entity {
///     int64 uuid                 = 1;
///     EEntityType ent_type        = 2;
///     AttrCollection attrs        = 3;
///     TempAttrCollection temp_attrs = 4;
///     ActorBodyPartInfos body_part_infos = 5;
///     SeqPassiveSkillInfo passive_skill_infos = 6;
///     BuffInfoSync buff_infos     = 7;
///     BuffEffectSync buff_effect  = 8;
///     EAppearType appear_type     = 9;
///     map&lt;int32, MagneticQueueAppearInfo&gt; magnetic_ride_queue_change_info_dict = 10;
///   }
///   message DisappearEntity {
///     int64 uuid = 1;
///     // other fields ignored
///   }
/// </code>
///
/// Both top-level helpers (<see cref="TryReadDisappearedUuids"/> and
/// <see cref="TryReadAppearAndDisappear"/>) silently swallow malformed
/// inner sub-messages (returning the list collected so far) — better to drop
/// one cache entry than to lose the whole list.
/// </summary>
internal static class SyncNearEntitiesReader
{
    public static bool TryReadDisappearedUuids(ReadOnlySpan<byte> payload, out IReadOnlyList<long> uuids)
    {
        var list = new List<long>(2);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire))
            {
                uuids = Array.Empty<long>();
                return false;
            }
            switch ((field, wire))
            {
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var de))
                    {
                        uuids = Array.Empty<long>();
                        return false;
                    }
                    if (TryReadDisappearUuid(de, out var u)) list.Add(u);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire))
                    {
                        uuids = Array.Empty<long>();
                        return false;
                    }
                    break;
            }
        }
        uuids = list;
        return true;
    }

    /// <summary>
    /// Parse both <c>appear</c> (field 1, repeated Entity) and <c>disappear</c>
    /// (field 2, repeated DisappearEntity) at once. Inside each appear Entity we
    /// extract only the uuid (field 1) and the <c>AttrCollection</c> sub-message
    /// (field 3, via <see cref="AttrCollectionReader.TryRead"/>); every other
    /// Entity field is skipped. A malformed inner sub-message is silently
    /// dropped — top-level returns true with whatever was successfully parsed.
    /// </summary>
    public static bool TryReadAppearAndDisappear(
        ReadOnlySpan<byte> payload,
        out IReadOnlyList<AppearEntityMsg> appears,
        out IReadOnlyList<long> disappearUuids)
    {
        var appearList    = new List<AppearEntityMsg>(2);
        var disappearList = new List<long>(2);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return Fail(out appears, out disappearUuids);
            switch ((field, wire))
            {
                case (1, 2): if (!ReadAppearList(payload, ref pos, appearList))     return Fail(out appears, out disappearUuids); break;
                case (2, 2): if (!ReadDisappearList(payload, ref pos, disappearList)) return Fail(out appears, out disappearUuids); break;
                default:     if (!WireProtocol.SkipField(payload, ref pos, wire))    return Fail(out appears, out disappearUuids); break;
            }
        }
        appears        = appearList;
        disappearUuids = disappearList;
        return true;

        static bool Fail(out IReadOnlyList<AppearEntityMsg> a, out IReadOnlyList<long> d)
        {
            a = Array.Empty<AppearEntityMsg>();
            d = Array.Empty<long>();
            return false;
        }
    }

    private static bool ReadAppearList(ReadOnlySpan<byte> payload, ref int pos, List<AppearEntityMsg> appearList)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var ae)) return false;
        if (TryReadEntity(ae, out var appearEntity)) appearList.Add(appearEntity);
        return true;
    }

    private static bool ReadDisappearList(ReadOnlySpan<byte> payload, ref int pos, List<long> disappearList)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var de)) return false;
        if (TryReadDisappearUuid(de, out var u)) disappearList.Add(u);
        return true;
    }

    /// <summary>Parse one wire <c>Entity</c> (uuid field 1 + AttrCollection field 3) — used for both AOI
    /// appears and EnterScene's PlayerEnt. Other Entity fields are skipped.</summary>
    internal static bool TryReadEntity(ReadOnlySpan<byte> payload, out AppearEntityMsg entity)
    {
        long uuid = 0;
        AttrCollectionMsg? attrs = null;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire))
            {
                entity = default;
                return false;
            }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var u))
                    {
                        entity = default;
                        return false;
                    }
                    uuid = (long)u;
                    break;

                case (3, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var attrBytes))
                    {
                        entity = default;
                        return false;
                    }
                    // Silently drop a malformed AttrCollection — we still want
                    // the uuid surfaced so the caller can register the entity.
                    if (AttrCollectionReader.TryRead(attrBytes, out var ac))
                    {
                        attrs = ac;
                    }
                    break;

                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire))
                    {
                        entity = default;
                        return false;
                    }
                    break;
            }
        }
        entity = new AppearEntityMsg(uuid, attrs);
        return true;
    }

    private static bool TryReadDisappearUuid(ReadOnlySpan<byte> payload, out long uuid)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { uuid = 0; return false; }
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) { uuid = 0; return false; }
                uuid = (long)u;
                return true;
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire)) { uuid = 0; return false; }
        }
        uuid = 0;
        return false;
    }
}
