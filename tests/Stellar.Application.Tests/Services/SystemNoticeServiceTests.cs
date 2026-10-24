using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>C#-side drain of the Lua system-notice queue (the Lua wrap itself is game-only).</summary>
public sealed class SystemNoticeServiceTests
{
    [Fact]
    public void NotInstalled_RunsInstallChunk_NoMessages()
    {
        var (svc, chat, lua, got, logs) = New(lua: null);

        svc.Tick(0.3f);
        chat.Drain();

        Assert.Empty(got);
        Assert.Equal(2, lua.Chunks.Count);   // poll, then install
        Assert.Contains("__stlr_sys_push", lua.Chunks[1]);
        Assert.Empty(logs);
    }

    [Fact]
    public void Records_PublishedAsSystemMessages_WithLabelTypeRef()
    {
        var (svc, chat, _, got, logs) = New(lua: "1\x1e" + "1\x1f" + "1001\x1fObtained\x1f<color=#fff>Guild Fund</color>×40"
                                                + "\x1e" + "2\x1f" + "\x1f\x1fServer maintenance soon");

        svc.Tick(0.3f);
        chat.Drain();

        Assert.Equal(2, got.Count);
        var a = got[0];
        Assert.Equal(ChatChannel.System, a.Channel);
        Assert.Equal(ChatMessageType.System, a.Type);
        Assert.False(a.IsHistory);
        Assert.Equal("Obtained", a.SystemLabel);
        Assert.Equal(1, a.SystemType);
        Assert.Equal(1001L, a.SystemRefId);
        Assert.Equal("<color=#fff>Guild Fund</color>×40", a.Text);
        Assert.True(a.MsgId < 0);
        Assert.Equal(DateTimeKindUtc(1_700_000_000_000), a.Timestamp);
        Assert.Equal(2, got[1].SystemType);
        Assert.Equal(string.Empty, got[1].SystemLabel);
        Assert.NotEqual(a.MsgId, got[1].MsgId);
        Assert.Contains("[ChatProbe] system-notice hook installed", logs);
    }

    [Fact]
    public void Throttled_BelowInterval_NoPoll()
    {
        var (svc, _, lua, _, _) = New(lua: "1");

        svc.Tick(0.1f);

        Assert.Empty(lua.Chunks);
    }

    [Fact]
    public void Lazy_NoChatConsumer_NoLua_ThenIdleAndResumeToggleTheFlag()
    {
        var chat = new ChatService(new StubLog());
        chat.AttachProbe(new StubProbe());
        var fake = new FakeLua { Out = "1" };
        var svc = new SystemNoticeService(fake, chat, () => 1_700_000_000_000, _ => { });

        svc.Tick(0.3f);
        Assert.Empty(fake.Chunks);                         // no subscriber, no RecentMessages read → never installed

        System.Action<ChatMessage> h = _ => { };
        chat.MessageReceived += h;
        svc.Tick(0.3f);
        Assert.Single(fake.Chunks);                        // first consumer: just the poll (install raises the flag)

        chat.MessageReceived -= h;
        svc.Tick(0.3f);
        Assert.Contains("__stlr_sys_on',nil", fake.Chunks[^1]);   // idle: Lua stops formatting + drops its queue

        chat.MessageReceived += h;
        svc.Tick(0.3f);
        Assert.Contains(fake.Chunks, c => c.Contains("__stlr_sys_on',true"));   // resumed before the poll
    }

    private static System.DateTime DateTimeKindUtc(long ms)
        => System.DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static (SystemNoticeService, ChatService, FakeLua, List<ChatMessage>, List<string>) New(string? lua)
    {
        var chat = new ChatService(new StubLog());
        chat.AttachProbe(new StubProbe());
        var got = new List<ChatMessage>();
        chat.MessageReceived += got.Add;
        var fake = new FakeLua { Out = lua };
        var logs = new List<string>();
        var svc = new SystemNoticeService(fake, chat, () => 1_700_000_000_000, logs.Add);
        return (svc, chat, fake, got, logs);
    }

    private sealed class FakeLua : ILua
    {
        public string? Out;
        public List<string> Chunks { get; } = new();
        public bool Ready => true;
        public void DoString(string chunk) => Chunks.Add(chunk);
        public bool TryReadGlobalBool(string key, out bool value) { value = false; return false; }
        public string? ReadGlobalString(string key) => key == "__stlr_sys_out" ? Out : null;
        public bool TryReadGlobalNumber(string key, out double value) { value = 0; return false; }
    }
}
