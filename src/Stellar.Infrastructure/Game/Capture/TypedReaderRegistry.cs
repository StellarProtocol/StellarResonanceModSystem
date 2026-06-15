using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game.Capture;

/// <summary>Named decode of a payload via existing hand-written readers, for the dump.</summary>
internal sealed class TypedReaderRegistry
{
    public sealed record TypedResult(string TypeName, Dictionary<string, object?> Fields);

    public TypedResult? TryDecode(ulong serviceUuid, uint methodId, WireMessageKind kind, ReadOnlySpan<byte> payload)
    {
        // Anonymous Returns: opportunistically try the roster reader. A success IS the identification.
        if (kind == WireMessageKind.Return && GetTeamInfoReplyReader.TryRead(payload, out var snap))
            return new TypedResult("GetTeamInfoReply", DescribeRoster(snap));

        return null;
    }

    private static Dictionary<string, object?> DescribeRoster(PartyWireSnapshot snap) => new()
    {
        ["PartyId"]      = snap.PartyId,
        ["LeaderCharId"] = snap.LeaderCharId,
        ["PartyType"]    = snap.PartyType.ToString(),
        ["IsMatching"]   = snap.IsMatching,
        ["MemberCount"]  = snap.Roster.Count,
        ["Members"]      = DescribeMembers(snap.Roster),
    };

    private static List<Dictionary<string, object?>> DescribeMembers(IReadOnlyList<PartyMemberRoster> roster)
    {
        var list = new List<Dictionary<string, object?>>(roster.Count);
        foreach (var m in roster)
            list.Add(new Dictionary<string, object?>
            {
                ["CharId"]  = m.CharId,
                ["SceneId"] = m.SceneId,
                ["GroupId"] = m.GroupId,
                ["Name"]    = m.Social?.Name,
                ["Hp"]      = m.FastSync?.Hp,
                ["MaxHp"]   = m.FastSync?.MaxHp,
            });
        return list;
    }
}
