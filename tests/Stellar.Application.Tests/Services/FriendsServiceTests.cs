using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>C#-side of <see cref="FriendsService"/>: string parsing, change detection, action queueing.</summary>
public sealed class FriendsServiceTests
{
    [Fact]
    public void ParseFriends_DecodesRow_Int64Exact()
    {
        var list = FriendsService.ParseFriends(Rows(
            R("9007199254740", "Karu", "bestie", "60", "5", "1", "1", "https://x/a.png"),
            R("42", "", "", "0", "0", "1", "0", "")));

        Assert.Equal(2, list.Count);
        var a = list[0];
        Assert.Equal(9_007_199_254_740L, a.CharId);
        Assert.Equal("Karu", a.Name);
        Assert.Equal("bestie", a.Remark);
        Assert.Equal(60, a.Level);
        Assert.Equal(5, a.ProfessionId);
        Assert.Equal(1, a.GroupId);
        Assert.True(a.IsOnline);
        Assert.Equal("https://x/a.png", a.AvatarUrl);
        Assert.False(list[1].IsOnline);
        Assert.Null(list[1].AvatarUrl);
    }

    [Fact]
    public void ParsePrivateChats_DecodesRow()
    {
        var list = FriendsService.ParsePrivateChats(Rows(
            R("777", "Mio", "1", "3", "hey <sprite=4>", "0", "777", "1779370145", "1"),
            R("888", "", "0", "0", "", "-1", "", "", "")));

        Assert.Equal(2, list.Count);
        var a = list[0];
        Assert.Equal(777L, a.CharId);
        Assert.Equal("Mio", a.Name);
        Assert.True(a.IsTop);
        Assert.Equal(3, a.UnreadCount);
        Assert.Equal("hey <sprite=4>", a.LatestText);
        Assert.Equal(0, a.LatestMsgType);
        Assert.Equal(777L, a.LatestSenderId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_779_370_145).UtcDateTime, a.LatestTimestampUtc);
        Assert.True(a.IsOnline);
        Assert.Equal(-1, list[1].LatestMsgType);
        Assert.Null(list[1].IsOnline);
        Assert.Equal(default, list[1].LatestTimestampUtc);
    }

    [Fact]
    public void Parse_BadIdRowsSkipped_EmptyIsEmpty()
    {
        Assert.Empty(FriendsService.ParseFriends(string.Empty));
        Assert.Empty(FriendsService.ParseFriends(R("abc", "X", "", "", "", "", "", "")));
        Assert.Empty(FriendsService.ParsePrivateChats(R("0", "", "", "", "", "", "", "", "")));
    }

    [Fact]
    public void Poll_RaisesChangedOnlyOnChange()
    {
        var lua = new FakeLua { Ok = true, Friends = R("1", "A", "", "1", "1", "1", "1", ""), Private = "" };
        var svc = new FriendsService(lua, _ => { });
        int changed = 0;
        svc.Changed += () => changed++;

        svc.Tick(0f);                 // first in-world tick polls immediately
        Assert.Equal(1, changed);
        Assert.Single(svc.Friends);

        svc.Tick(2.6f);               // same strings → no rebuild
        Assert.Equal(1, changed);

        lua.Private = R("5", "B", "0", "1", "hi", "0", "5", "1", "");
        svc.Tick(2.6f);
        Assert.Equal(2, changed);
        Assert.Single(svc.PrivateChats);
    }

    [Fact]
    public void NotInstalled_RunsInstallChunk()
    {
        var lua = new FakeLua { Ok = false };
        var svc = new FriendsService(lua, _ => { });

        svc.Tick(0f);

        Assert.Equal(2, lua.Chunks.Count);
        Assert.Contains("__stlr_fr_poll", lua.Chunks[1]);
    }

    [Fact]
    public void Actions_QueueThenRunInCoroutine()
    {
        var lua = new FakeLua { Ok = true, Friends = "", Private = "" };
        IFriends svc = new FriendsService(lua, _ => { });

        svc.CreatePrivateChat(123456789012);
        svc.MarkPrivateRead(0);       // invalid id dropped
        Assert.Empty(lua.Chunks);
        ((FriendsService)svc).Tick(0f);

        Assert.Contains(lua.Chunks, c => c.Contains("create_coro_xpcall") && c.Contains("local cid = 123456789012 ")
                                         && c.Contains("AsyncCreatePrivateChat"));
        Assert.DoesNotContain(lua.Chunks, c => c.Contains("AsyncSetPrivateChatHasRead"));
    }

    // Built with Join, NOT escaped literals: C# hex escapes are greedy (up to 4 hex digits), so a field separator
    // literal followed by "be…" would silently become a different character.
    private static string R(params string[] fields) => string.Join((char)0x1f, fields);
    private static string Rows(params string[] rows) => string.Join((char)0x1e, rows);

    private sealed class FakeLua : ILua
    {
        public bool Ok;
        public string? Friends;
        public string? Private;
        public List<string> Chunks { get; } = new();
        public bool Ready => true;
        public void DoString(string chunk) => Chunks.Add(chunk);
        public bool TryReadGlobalBool(string key, out bool value) { value = Ok; return key == "__stlr_fr_ok" && Ok; }
        public string? ReadGlobalString(string key) => key == "__stlr_fr_f" ? Friends : key == "__stlr_fr_p" ? Private : null;
        public bool TryReadGlobalNumber(string key, out double value) { value = 0; return false; }
    }
}
