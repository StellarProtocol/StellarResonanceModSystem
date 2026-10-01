using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 4 emotes + § 6 IEmotes: the wheel's own list and play path through the Lua VM; never a built packet.
public sealed class EmoteServiceTests
{
    private sealed class FakeLua : ILua
    {
        public bool Ready { get; set; } = true;
        public readonly List<string> Chunks = new();
        public string? Next;
        public void DoString(string chunk) => Chunks.Add(chunk);
        public bool TryReadGlobalBool(string key, out bool value) { value = false; return false; }
        public string? ReadGlobalString(string key) => key == EmoteService.OutGlobal ? Next : null;
        public bool TryReadGlobalNumber(string key, out double value) { value = 0; return false; }
    }

    private const string TwoRows = "ok 9011\tWave\tui/atlas/emote_icon/emo_icon_action_goodbyef\t0\n9001\tSit\tui/atlas/emote_icon/sit\t1";

    [Fact]
    public void Refresh_parses_the_wheel_list_and_raises_once()
    {
        var lua = new FakeLua { Next = TwoRows };
        var s = new EmoteService(lua, _ => { });
        var raised = 0;
        s.UnlockedChanged += () => raised++;
        s.Refresh();
        s.Refresh();
        Assert.Equal(1, raised);
        Assert.Equal(new[] { 9011, 9001 }, new[] { s.Unlocked[0].Id, s.Unlocked[1].Id });
        Assert.Equal("Wave", s.Unlocked[0].Name);
        Assert.False(s.Unlocked[0].Looping);
        Assert.True(s.Unlocked[1].Looping);
        Assert.Contains("GetExpressionShowDataByType", lua.Chunks[0]);
    }

    [Fact]
    public void Refresh_does_nothing_until_lua_is_ready()
    {
        var lua = new FakeLua { Ready = false, Next = TwoRows };
        var s = new EmoteService(lua, _ => { });
        s.Refresh();
        Assert.Empty(lua.Chunks);
        Assert.Empty(s.Unlocked);
    }

    [Fact]
    public void A_lua_error_keeps_the_previous_list_and_warns()
    {
        var lua = new FakeLua { Next = TwoRows };
        var warn = new List<string>();
        var s = new EmoteService(lua, warn.Add);
        s.Refresh();
        lua.Next = "err attempt to index a nil value";
        s.Refresh();
        Assert.Equal(2, s.Unlocked.Count);
        Assert.Single(warn);
    }

    [Theory]
    [InlineData("ok played", EmoteResult.Played)]
    [InlineData("ok refused", EmoteResult.Refused)]
    [InlineData("ok novm", EmoteResult.Unavailable)]
    [InlineData("err boom", EmoteResult.Failed)]
    [InlineData(null, EmoteResult.Failed)]
    public void Play_maps_the_vm_answer(string? answer, EmoteResult expected)
    {
        var lua = new FakeLua { Next = answer };
        Assert.Equal(expected, new EmoteService(lua, _ => { }).PlayAsync(9011).Result);
    }

    [Fact]
    public void Play_uses_the_wheels_own_check_then_play_and_never_builds_a_packet()
    {
        var lua = new FakeLua { Next = "ok played" };
        new EmoteService(lua, _ => { }).PlayAsync(9011).Wait();
        var chunk = lua.Chunks[0];
        Assert.Contains("CheckEmoteCondition(9011, true)", chunk);
        Assert.Contains("PlayAction(9011, true, false)", chunk);
        Assert.DoesNotContain("Send", chunk);
        Assert.DoesNotContain("Proxy", chunk);
    }

    [Fact]
    public void Play_when_lua_is_not_ready_is_unavailable_without_a_call()
    {
        var lua = new FakeLua { Ready = false };
        Assert.Equal(EmoteResult.Unavailable, new EmoteService(lua, _ => { }).PlayAsync(9011).Result);
        Assert.Empty(lua.Chunks);
    }

    [Fact]
    public void Malformed_rows_are_skipped()
    {
        var list = EmoteLua.ParseList("9011\tWave\ticon\t0\nbad\nx\ty\tz\t0\n12\tOnlyName");
        Assert.Single(list);
        Assert.Equal(9011, list[0].Id);
    }

    [Fact]
    public void Facade_release_drops_its_handlers()
    {
        var lua = new FakeLua { Next = TwoRows };
        var s = new EmoteService(lua, _ => { });
        var p = new PluginEmotes(s);
        var calls = 0;
        p.UnlockedChanged += () => calls++;
        p.ReleaseAll();
        s.Refresh();
        Assert.Equal(0, calls);
    }
}
