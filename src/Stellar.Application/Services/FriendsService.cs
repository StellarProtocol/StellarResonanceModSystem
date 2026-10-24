using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// <see cref="IFriends"/> over the game's Lua data (<c>friend_main_data</c> / <c>chat_main_data</c> — there is no C#
/// friend roster). Read-only polling through the shared <see cref="ILua"/> bridge: every <see cref="PollInterval"/>
/// one tiny <c>DoString</c> runs an installed Lua walker that serializes both lists into two globals, then two string
/// reads; each list is rebuilt ONLY when its serialized string differs from the last poll (an unchanged poll costs
/// the two reads + a string compare, no parsing). Actions are queued (any thread) and run on the next tick as the
/// game's own coroutine VM calls. No Lua→C# callback anywhere (it crashes the IL2CPP runtime).
/// <para>Ticked from the IsWorldActive-gated framework <c>Update</c>; main thread only inside.</para>
/// </summary>
internal sealed partial class FriendsService : IFriends
{
    private const float PollInterval = 2.5f;
    private const float PostActionDelay = 0.75f;   // re-poll this soon after an action (the RPC needs a beat)
    private const char RecordSep = '\x1e';
    private const char FieldSep = '\x1f';

    private enum ActionKind { History, Read, Create, Close }

    private readonly ILua _lua;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<(ActionKind Kind, long CharId)> _actions = new();

    private float _accum = PollInterval;   // first in-world tick polls immediately
    private bool _installLogged;
    private string? _lastFriendsRaw;
    private string? _lastPrivateRaw;
    private IReadOnlyList<FriendInfo> _friends = Array.Empty<FriendInfo>();
    private IReadOnlyList<PrivateChatTarget> _privateChats = Array.Empty<PrivateChatTarget>();
    private Action? _changed;
    private readonly object _handlersLock = new();

    public FriendsService(ILua lua, Action<string> log)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<FriendInfo> Friends => _friends;
    public IReadOnlyList<PrivateChatTarget> PrivateChats => _privateChats;

    public event Action Changed
    {
        add { lock (_handlersLock) _changed += value; }
        remove { lock (_handlersLock) _changed -= value; }
    }

    public void RequestPrivateHistory(long charId) => Enqueue(ActionKind.History, charId);
    public void MarkPrivateRead(long charId) => Enqueue(ActionKind.Read, charId);
    public void CreatePrivateChat(long charId) => Enqueue(ActionKind.Create, charId);
    public void ClosePrivateChat(long charId) => Enqueue(ActionKind.Close, charId);

    private void Enqueue(ActionKind kind, long charId)
    {
        if (charId <= 0 || _actions.Count >= 64) return;
        _actions.Enqueue((kind, charId));
    }

    /// <summary>Framework Update tick (world-gated by the caller).</summary>
    public void Tick(float deltaTime)
    {
        if (!_lua.Ready) return;
        try
        {
            if (RunQueuedActions()) _accum = Math.Max(_accum, PollInterval - PostActionDelay);
            _accum += deltaTime;
            if (_accum < PollInterval) return;
            _accum = 0f;
            Poll();
        }
        catch (Exception ex) { _log($"[Friends] tick threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    private bool RunQueuedActions()
    {
        bool any = false;
        while (_actions.TryDequeue(out var a))
        {
            var body = a.Kind switch
            {
                ActionKind.History => HistoryBody,
                ActionKind.Read => ReadBody,
                ActionKind.Create => CreateBody,
                _ => CloseBody,
            };
            _lua.DoString(ActionPrologue + a.CharId.ToString(CultureInfo.InvariantCulture) + " " + body + ActionEpilogue);
            _log($"[Friends] {a.Kind} issued");   // no charId: who you whisper is private (shared log)
            any = true;
        }
        return any;
    }

    private void Poll()
    {
        _lua.DoString(PollChunk);
        if (!_lua.TryReadGlobalBool(OkGlobal, out var ok) || !ok)
        {
            _lua.DoString(InstallChunk);   // walker absent (first run / Lua globals reset) → next poll uses it
            _accum = PollInterval - 0.25f;
            return;
        }
        if (!_installLogged)
        {
            _installLogged = true;
            _log("[Friends] friend/private-chat poll active");
        }

        bool changed = false;
        var f = _lua.ReadGlobalString(FriendsGlobal) ?? string.Empty;
        if (!string.Equals(f, _lastFriendsRaw, StringComparison.Ordinal))
        {
            _lastFriendsRaw = f;
            _friends = ParseFriends(f);
            changed = true;
        }
        var p = _lua.ReadGlobalString(PrivateGlobal) ?? string.Empty;
        if (!string.Equals(p, _lastPrivateRaw, StringComparison.Ordinal))
        {
            _lastPrivateRaw = p;
            _privateChats = ParsePrivateChats(p);
            changed = true;
        }
        if (changed) RaiseChanged();
    }

    private void RaiseChanged()
    {
        var h = _changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); }
            catch (Exception ex) { _log($"[Friends] Changed subscriber threw: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    internal static IReadOnlyList<FriendInfo> ParseFriends(string raw)
    {
        if (raw.Length == 0) return Array.Empty<FriendInfo>();
        var rows = raw.Split(RecordSep);
        var list = new List<FriendInfo>(rows.Length);
        foreach (var row in rows)
        {
            var c = row.Split(FieldSep);
            if (c.Length < 8 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            list.Add(new FriendInfo(id, c[1], c[2], Int(c[3]), Int(c[4]), Int(c[5]), c[6] == "1", c[7].Length > 0 ? c[7] : null));
        }
        return list.ToArray();
    }

    internal static IReadOnlyList<PrivateChatTarget> ParsePrivateChats(string raw)
    {
        if (raw.Length == 0) return Array.Empty<PrivateChatTarget>();
        var rows = raw.Split(RecordSep);
        var list = new List<PrivateChatTarget>(rows.Length);
        foreach (var row in rows)
        {
            var c = row.Split(FieldSep);
            if (c.Length < 9 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            long ts = Long(c[7]);
            bool? online = c[8] switch { "1" => true, "0" => false, _ => null };
            list.Add(new PrivateChatTarget(
                id, c[1], c[2] == "1", Int(c[3]), c[4], c[5].Length > 0 ? Int(c[5]) : -1, Long(c[6]),
                ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : default, online));
        }
        return list.ToArray();
    }

    private static int Int(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long Long(string s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;
}
