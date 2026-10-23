using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Surfaces the game's CLIENT-GENERATED system notices ("Obtained Guild Fund×40", item-cap warnings, broadcasts…)
/// on <see cref="IChat.MessageReceived"/> as <see cref="ChatChannel.System"/> messages. They never go over the
/// network (the chat probe can't see them): the game builds them in Lua and hands each to
/// <c>Z.VMMgr.GetVM("chat_main").SetReceiveSystemMsg(info)</c> (<c>chat_main_vm.lua:726</c>; every caller re-looks
/// the method up on the VM table per call, so a table-field wrap catches them all). We wrap that method in Lua to
/// call the original, format the record with the game's own <c>Z.ChatMsgHelper.GetSystemType</c> and queue it; this
/// service POLLS and drains the queue (Lua→C# native callbacks crash the game, so the data only flows C#-pulled).
/// <para>Ticked from the IsWorldActive-gated framework <c>Update</c>, so it only runs in-world (notices raised while
/// loading wait in the Lua queue, capped at 64). Every <see cref="PollInterval"/> one tiny <c>DoString</c> + one string
/// read; the drain also re-checks the wrap's identity (scene reload / reconnect replacing the VM table or method
/// re-wraps it) — a single table-field compare in Lua. Main thread only.</para>
/// </summary>
internal sealed partial class SystemNoticeService
{
    private const float PollInterval = 0.25f;
    private const char RecordSep = '\x1e';
    private const char FieldSep = '\x1f';

    private readonly ILua _lua;
    private readonly ChatService _chat;
    private readonly Func<long> _serverNowMs;
    private readonly Action<string> _log;

    private float _accum;
    private long _lastGen;
    private long _syntheticId;

    public SystemNoticeService(ILua lua, ChatService chat, Func<long> serverNowMs, Action<string> log)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _serverNowMs = serverNowMs ?? throw new ArgumentNullException(nameof(serverNowMs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Framework Update tick (world-gated by the caller). Throttles itself to <see cref="PollInterval"/>.</summary>
    public void Tick(float deltaTime)
    {
        _accum += deltaTime;
        if (_accum < PollInterval) return;
        _accum = 0f;
        if (!_lua.Ready) return;
        try { Poll(); }
        catch (Exception ex) { _log($"[ChatProbe] system-notice poll threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Poll()
    {
        _lua.DoString(PollChunk);
        var raw = _lua.ReadGlobalString(OutGlobal);
        if (raw is null)
        {
            // Drain function absent (first run, or the Lua globals were reset) → install; it runs from the next poll.
            _lua.DoString(InstallChunk);
            return;
        }
        Publish(raw);
    }

    // raw = "<gen>" or "<gen>\x1e<rec>\x1e<rec>…", rec = "type\x1fid\x1flabel\x1fcontent".
    private void Publish(string raw)
    {
        var parts = raw.Split(RecordSep);
        if (long.TryParse(parts[0], out var gen) && gen != _lastGen) NoteWrap(gen);
        if (parts.Length < 2) return;

        var nowMs = _serverNowMs();
        var stamp = nowMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime : DateTime.UtcNow;
        for (int i = 1; i < parts.Length; i++)
        {
            var msg = ToMessage(parts[i], stamp);
            if (msg is not null) _chat.PublishLocal(msg);
        }
    }

    private void NoteWrap(long gen)
    {
        if (gen <= 0) return;
        _log(_lastGen == 0
            ? "[ChatProbe] system-notice hook installed"
            : $"[ChatProbe] system-notice hook re-installed (VM table/method replaced; gen={gen})");
        _lastGen = gen;
    }

    private ChatMessage? ToMessage(string record, DateTime stamp)
    {
        var f = record.Split(FieldSep);
        if (f.Length < 4) return null;
        var content = f[3];
        var label = f[2];
        if (content.Length == 0 && label.Length == 0) return null;
        int.TryParse(f[0], out var type);
        long.TryParse(f[1], out var refId);
        return new ChatMessage(
            Channel:    ChatChannel.System,
            SenderName: string.Empty,
            SenderId:   0L,
            Text:       content,   // rich-text colour tags kept on purpose — consumers render TMP rich text
            Timestamp:  stamp,
            Type:       ChatMessageType.System,
            RawPayload: ReadOnlyMemory<byte>.Empty,
            Sender:     null,
            MsgId:      --_syntheticId,   // negative: never collides with a server msg_id
            IsHistory:  false)
        { SystemLabel = label, SystemType = type, SystemRefId = refId };
    }
}
