using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Party;

public sealed class TeamControlChunkTests
{
    [Fact]
    public void BuildMoveChunk_EmitsGroupCharIdSlot_InOrder()
    {
        // group 2 (1-based), charId 12345, slot 0 (0-based) → AsyncUpdateTeamGroup(2, 12345, 0)
        var chunk = PandaTeamControlProbe.BuildMoveChunk(group: 2, charId: 12345L, slot: 0);
        Assert.Contains("AsyncUpdateTeamGroup", chunk);
        Assert.Contains("(2, 12345, 0)", chunk);
        Assert.Contains("pcall", chunk);
    }

    [Fact]
    public void BuildMoveChunk_LastCell_Group4Slot4()
    {
        var chunk = PandaTeamControlProbe.BuildMoveChunk(group: 4, charId: 9L, slot: 4);
        Assert.Contains("(4, 9, 4)", chunk);
    }
}
