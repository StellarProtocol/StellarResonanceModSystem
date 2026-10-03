using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Runs <see cref="PosingLua"/> through the game's Lua VM. Every query is a one-shot read on a user action.
/// Not ready → the safe answer (refuse, unknown gender, no model, no names, no expressions). Main thread.</summary>
internal sealed class PoseLuaQueries
{
    private readonly ILua _lua;

    public PoseLuaQueries(ILua lua) => _lua = lua;

    public bool Allowed(int actionId) => PosingLua.ParseTrue(Ask(PosingLua.CheckChunk(actionId)));
    public int Gender(long uuid, bool self) => PosingLua.ParseGender(Ask(PosingLua.GenderChunk(uuid, self)));
    public int NpcModelId(long uuid) => PosingLua.ParseInt(Ask(PosingLua.NpcModelChunk(uuid)));
    public Dictionary<long, string> Names(IReadOnlyList<long> uuids) => PosingLua.ParseNames(Ask(PosingLua.NamesChunk(uuids)), uuids);
    public IReadOnlyList<ExpressionInfo> Expressions() => PosingLua.ParseExpressions(Ask(PosingLua.ExpressionsChunk));
    public int MemberLimit() => PosingLua.ParseInt(Ask(PosingLua.MemberLimitChunk));

    private string? Ask(string body)
    {
        if (!_lua.Ready) return null;
        _lua.DoString(PosingLua.Wrap(body));
        return _lua.ReadGlobalString(PosingLua.OutGlobal);
    }
}
