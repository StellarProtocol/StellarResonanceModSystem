using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Services;

/// <summary>Unlocked emotes + play through the game's own emote VM (spec § 4). <see cref="Refresh"/> runs on login and
/// on zone changes once the world is stable (event-driven, never polled). Main thread.</summary>
internal sealed class EmoteService : IEmotes
{
    internal const string OutGlobal = "__stellar_fc_out";

    private readonly ILua _lua;
    private readonly Action<string> _warn;
    private IReadOnlyList<EmoteInfo> _unlocked = Array.Empty<EmoteInfo>();

    public EmoteService(ILua lua, Action<string> warn)
    {
        _lua = lua;
        _warn = warn;
    }

    public IReadOnlyList<EmoteInfo> Unlocked => _unlocked;
    public event Action? UnlockedChanged;

    public Task<EmoteResult> PlayAsync(int id) => Task.FromResult(Play(id));

    internal void Refresh()
    {
        if (!_lua.Ready) return;
        var answer = Run(EmoteLua.ListChunk);
        if (answer is null || !answer.StartsWith("ok ", StringComparison.Ordinal))
        {
            _warn("could not read the emote list: " + (answer ?? "no answer"));
            return;
        }
        var list = EmoteLua.ParseList(answer.Substring(3));
        if (Same(list, _unlocked)) return;
        _unlocked = list;
        UnlockedChanged?.Invoke();
    }

    private EmoteResult Play(int id)
    {
        if (!_lua.Ready) return EmoteResult.Unavailable;
        var result = EmoteLua.ParsePlay(Run(EmoteLua.PlayChunk(id)));
        if (result == EmoteResult.Failed) _warn($"emote {id} failed");
        return result;
    }

    private string? Run(string body)
    {
        _lua.DoString(EmoteLua.Wrap(body));
        return _lua.ReadGlobalString(OutGlobal);
    }

    private static bool Same(IReadOnlyList<EmoteInfo> a, IReadOnlyList<EmoteInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name) return false;
        return true;
    }
}
