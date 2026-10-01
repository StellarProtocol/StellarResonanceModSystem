using System.Collections.Generic;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// The Lua the posing backend runs (recon photo-posing-recon.md §§ 1, run 5 (2)): the expression list (emote table Type 2,
// FaceDataId[1|2]), names (AttrName, NPC table fallback), gender (charBase / AttrGender vs EGender.GenderMale), the NPC
// model id (AttrId → NpcTableMgr.ModelID) and the game's own emote check.
public sealed class PosingLuaTests
{
    [Fact]
    public void Wrap_stores_ok_or_err_in_its_own_global()
    {
        var chunk = PosingLua.Wrap("return 1");
        Assert.Contains("rawset(_G,'__stellar_pz_out'", chunk);
        Assert.Contains("pcall(function() return 1 end)", chunk);
    }

    [Fact]
    public void Expressions_parse_and_skip_broken_rows()
    {
        var list = PosingLua.ParseExpressions("ok 1003\tAngry\t303\t403\n1015\tStartled\t315\t0\nbad\n0\tZero\t1\t2\n1020\tBlank\t0\t0");
        Assert.Equal(2, list.Count);
        Assert.Equal((1003, "Angry", 303, 403), (list[0].Id, list[0].Name, list[0].MaleFaceId, list[0].FemaleFaceId));
        Assert.Equal((315, 315), (list[1].MaleFaceId, list[1].FemaleFaceId));   // one missing gender falls back to the other
        Assert.Empty(PosingLua.ParseExpressions("err no vm"));
        Assert.Empty(PosingLua.ParseExpressions(null));
    }

    [Fact]
    public void Names_map_by_position()
    {
        var uuids = new List<long> { 11, 22, 33 };
        var names = PosingLua.ParseNames("ok Revette\n\nCelia", uuids);
        Assert.Equal("Revette", names[11]);
        Assert.Equal("", names[22]);
        Assert.Equal("Celia", names[33]);
        Assert.Equal("", PosingLua.ParseNames("err x", uuids)[22]);
    }

    [Theory]
    [InlineData("ok 1", 1)]
    [InlineData("ok 2", 2)]
    [InlineData("ok 0", 1)]
    [InlineData("err nope", 1)]
    [InlineData(null, 1)]
    public void Gender_is_1_male_2_female_and_unknown_is_1(string? answer, int expected) =>
        Assert.Equal(expected, PosingLua.ParseGender(answer));

    [Fact]
    public void Numbers_and_booleans_parse()
    {
        Assert.Equal(260031, PosingLua.ParseInt("ok 260031"));
        Assert.Equal(0, PosingLua.ParseInt("ok nil"));
        Assert.Equal(0, PosingLua.ParseInt(null));
        Assert.True(PosingLua.ParseTrue("ok true"));
        Assert.False(PosingLua.ParseTrue("ok false"));
        Assert.False(PosingLua.ParseTrue("ok novm"));
    }

    [Fact]
    public void Chunks_carry_the_recon_calls()
    {
        Assert.Contains("GetExpressionShowDataByType(4, false, nil, true)", PosingLua.ExpressionsChunk);
        Assert.Contains("r.Type == 2", PosingLua.ExpressionsChunk);
        Assert.Contains("FaceDataId[1]", PosingLua.ExpressionsChunk);
        Assert.Contains("GetEntity(42)", PosingLua.GenderChunk(42, self: false));
        Assert.Contains("AttrGender", PosingLua.GenderChunk(42, self: false));
        Assert.Contains("charBase.gender", PosingLua.GenderChunk(0, self: true));
        Assert.Contains("'EGender', 'GenderMale'", PosingLua.GenderChunk(0, self: true));
        Assert.Contains("NpcTableMgr", PosingLua.NpcModelChunk(7));
        Assert.Contains("ModelID", PosingLua.NpcModelChunk(7));
        Assert.Contains("CheckEmoteCondition(9020, true)", PosingLua.CheckChunk(9020));
        Assert.Contains("{5,6}", PosingLua.NamesChunk(new List<long> { 5, 6 }));
        Assert.Contains("AttrName", PosingLua.NamesChunk(new List<long> { 5 }));
        Assert.Contains("PhotographTeamMemberLimit", PosingLua.MemberLimitChunk);
        Assert.Contains("Z.IsPCUI and l[1]", PosingLua.MemberLimitChunk);
    }
}
