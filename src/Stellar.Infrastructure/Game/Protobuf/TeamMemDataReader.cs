using System;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Pure parser for the <c>TeamMemData</c> protobuf shape exchanged on the
/// team-member roster wire. Nested sub-message parsers follow in the same file
/// (per the nested sub-message parser convention).
///
/// <code>
///   message TeamMemData {
///     int64 char_id       = 1;
///     int32 enter_time    = 2;
///     int32 call_status   = 3;   // skipped
///     int32 talent_id     = 4;   // skipped
///     int32 online_status = 5;
///     int32 scene_id      = 6;
///     bool  voice_is_open = 7;   // skipped
///     int32 group_id      = 8;
///     TeamMemberSocialData social_data = 9;
///   }
/// </code>
/// </summary>
internal static class TeamMemDataReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out PartyMemberRoster roster)
    {
        long  charId = 0;
        int   enterTime = 0, onlineStatus = 0, sceneId = 0, groupId = 0, talentId = 0;
        PartyMemberSocialSync? social = null;
        int   p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { roster = default!; return false; }

            switch ((field, wire))
            {
                case (1, 0): if (!TryReadCharId(payload, ref p, out charId))                 { roster = default!; return false; } break;
                case (2, 0): if (!TryReadEnterTime(payload, ref p, out enterTime))           { roster = default!; return false; } break;
                case (4, 0): if (!WireProtocol.TryReadVarint(payload, ref p, out var tv))     { roster = default!; return false; } talentId = (int)tv; break;
                case (5, 0): if (!TryReadOnlineStatus(payload, ref p, out onlineStatus))     { roster = default!; return false; } break;
                case (6, 0): if (!TryReadSceneId(payload, ref p, out sceneId))               { roster = default!; return false; } break;
                case (8, 0): if (!TryReadGroupId(payload, ref p, out groupId))               { roster = default!; return false; } break;
                case (9, 2): if (!TryReadSocial(payload, ref p, groupId, out social))        { roster = default!; return false; } break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire))                       { roster = default!; return false; }
                    break;
            }
        }

        roster = new PartyMemberRoster(
            CharId:          charId,
            EnterTimeRaw:    enterTime,
            OnlineStatusRaw: onlineStatus,
            SceneId:         sceneId,
            GroupId:         groupId,
            FastSync:        null,
            Social:          social,
            TalentId:        talentId);
        return true;
    }

    private static bool TryReadCharId(ReadOnlySpan<byte> payload, ref int p, out long charId)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { charId = 0; return false; }
        charId = (long)v;
        return true;
    }

    private static bool TryReadEnterTime(ReadOnlySpan<byte> payload, ref int p, out int enterTime)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { enterTime = 0; return false; }
        enterTime = (int)v;
        return true;
    }

    private static bool TryReadOnlineStatus(ReadOnlySpan<byte> payload, ref int p, out int onlineStatus)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { onlineStatus = 0; return false; }
        onlineStatus = (int)v;
        return true;
    }

    private static bool TryReadSceneId(ReadOnlySpan<byte> payload, ref int p, out int sceneId)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { sceneId = 0; return false; }
        sceneId = (int)v;
        return true;
    }

    private static bool TryReadGroupId(ReadOnlySpan<byte> payload, ref int p, out int groupId)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { groupId = 0; return false; }
        groupId = (int)v;
        return true;
    }

    private static bool TryReadSocial(ReadOnlySpan<byte> payload, ref int p, int groupId, out PartyMemberSocialSync? social)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var socBytes)) { social = null; return false; }
        return TeamMemberSocialDataReader.TryRead(socBytes, groupId, out social);
    }
}

/// <summary>
/// Parser for <c>TeamMemberSocialData</c>. Only fields 1 (basic_data) and 4
/// (profession_data) are projected; the rest (avatar, face, equip, etc.) are
/// skipped.
///
/// <code>
///   message TeamMemberSocialData {
///     BasicData       basic_data       = 1;
///     AvatarInfo      avatar_info      = 2;   // skipped
///     FaceData        face_data        = 3;   // skipped
///     ProfessionData  profession_data  = 4;
///     EquipData       equip_data       = 5;   // skipped
///     // ... (remaining fields skipped)
///   }
/// </code>
/// </summary>
internal static class TeamMemberSocialDataReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, int groupId, out PartyMemberSocialSync? social)
    {
        string? name   = null;
        int     level  = 0;
        int     profession = 0;
        int     p      = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { social = null; return false; }

            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var basicBytes)) { social = null; return false; }
                    if (!BasicDataReader.TryRead(basicBytes, out name, out level)) { social = null; return false; }
                    break;
                case (4, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var profBytes)) { social = null; return false; }
                    if (!ProfessionDataReader.TryRead(profBytes, out profession)) { social = null; return false; }
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { social = null; return false; }
                    break;
            }
        }

        social = new PartyMemberSocialSync(name, level, profession, groupId);
        return true;
    }
}

/// <summary>
/// Parser for <c>BasicData</c> nested inside <c>TeamMemberSocialData</c>. Field
/// numbers confirmed against the StarResonanceData proto (stru_basic_data.proto):
/// <list type="bullet">
///   <item>1 (int64)   — char_id</item>
///   <item>3 (string)  — name</item>
///   <item>6 (int32)   — level</item>
/// </list>
/// (The earlier 1=name / 2=level were placeholder guesses and never matched the
/// wire — every party member's name came back empty.)
/// </summary>
internal static class BasicDataReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out string? name, out int level)
    {
        name  = null;
        level = 0;
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;

            switch ((field, wire))
            {
                case (3, 2):
                    if (!WireProtocol.TryReadString(payload, ref p, out var n)) return false;
                    name = n;
                    break;
                case (6, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) return false;
                    level = (int)v;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
                    break;
            }
        }
        return true;
    }
}

/// <summary>
/// Parser for <c>ProfessionData</c> nested inside <c>TeamMemberSocialData</c>.
/// Field number is a placeholder best-guess pending live in-game bring-up:
/// <list type="bullet">
///   <item>1 (varint) — profession_id (placeholder)</item>
/// </list>
/// </summary>
internal static class ProfessionDataReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out int professionId)
    {
        professionId = 0;
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;

            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) return false;
                    professionId = (int)v;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
                    break;
            }
        }
        return true;
    }
}
