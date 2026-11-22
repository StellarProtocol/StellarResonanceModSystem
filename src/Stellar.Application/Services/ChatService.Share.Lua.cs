using System;
using System.Diagnostics;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Lua plumbing behind <see cref="ChatService"/>'s position share: run a one-line call, re-define the
/// compiled-once functions when the call reports them missing, read the parked result, and poll the share verdict.</summary>
internal sealed partial class ChatService
{
    // Preview: "sid|line|x|y|z|layer|can" (or "none" / "err"); the scene name comes back in a second global.
    private string? RunPreview(out string? sceneName)
    {
        var lua = Lua!;
        lua.DoString(ChatShareLua.CallPreview);
        var r = lua.ReadGlobalString(ChatShareLua.ResultKey);
        if (r == "nofn")
        {
            lua.DoString(ChatShareLua.DefinePreview);
            lua.DoString(ChatShareLua.CallPreview);
            r = lua.ReadGlobalString(ChatShareLua.ResultKey);
        }
        sceneName = r is null or "none" or "err" ? null : lua.ReadGlobalString(ChatShareLua.NameKey);
        return r;
    }

    private static bool TryUnpackPreview(string? packed, string? sceneName, out ChatPosition? pos, out bool canShare)
    {
        pos = null;
        canShare = false;
        if (packed is null) return false;
        var p = packed.Split('|');
        if (p.Length != 7) return false;
        var v = new int[7];
        for (int i = 0; i < 7; i++)
            if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
        pos = new ChatPosition
        {
            SceneId = v[0], LineId = v[1], X = v[2], Y = v[3], Z = v[4], MapLayerId = v[5],
            SceneName = string.IsNullOrEmpty(sceneName) ? null : ChatRichText.Sanitize(sceneName),
        };
        canShare = v[6] == 1;
        return true;
    }

    // Share: call; the sentinel is true = started, false = functions missing (define + retry once), absent = threw.
    private bool RunDefined(string call, string sentKey, string define)
    {
        var lua = Lua!;
        lua.DoString(call);
        if (lua.TryReadGlobalBool(sentKey, out var sent) && !sent)
        {
            lua.DoString(define);
            lua.DoString(call);
            return lua.TryReadGlobalBool(sentKey, out sent) && sent;
        }
        return sent;
    }

    // Track: 0 / 2 / 3 from the function, -1 threw, -2 missing (define + retry once). Unreadable → -1.
    private int RunTrack(string call)
    {
        var lua = Lua!;
        lua.DoString(call);
        if (!lua.TryReadGlobalNumber(ChatShareLua.TrackKey, out var code)) return -1;
        if (code == -2)
        {
            lua.DoString(ChatShareLua.DefineTrack);
            lua.DoString(call);
            if (!lua.TryReadGlobalNumber(ChatShareLua.TrackKey, out code)) return -1;
        }
        return (int)code;
    }

    // From Drain (main thread). Free when nothing is pending; ≤10 Hz reads of one Lua global while a reply is due.
    private void PollShareResult()
    {
        if (_shareDeadline == 0) return;
        var now = Stopwatch.GetTimestamp();
        if (now < _shareNextPoll) return;
        _shareNextPoll = now + SharePollTicks;
        var lua = Lua;
        if (lua is not null && lua.TryReadGlobalNumber(ChatShareLua.ShareErrKey, out var code))
        {
            _shareDeadline = 0;
            int err = (int)code;
            DispatchSendResult(new ChatSendResult(_shareChannel, err == 0, err, null)
                { ViaFramework = true, IsPositionShare = true });
            return;
        }
        if (now > _shareDeadline)
        {
            _shareDeadline = 0;   // no verdict (RPC stuck / Lua reset): report "unknown", like a send that never left
            DispatchSendResult(new ChatSendResult(_shareChannel, false, ChatSendResult.ErrorNotSent, null)
                { ViaFramework = true, IsPositionShare = true });
        }
    }
}
