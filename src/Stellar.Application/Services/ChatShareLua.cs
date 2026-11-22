using System.Globalization;
using System.Text;

namespace Stellar.Application.Services;

/// <summary>
/// Lua for <see cref="ChatService"/>'s position share / track (game paths in <c>Chat-Hypertext-Links.md</c>). Each
/// body is compiled ONCE into a <c>_G</c> function; calls are one-line chunks that report "function missing" through
/// a sentinel so the caller re-defines after a Lua reset (<c>Lua-Injection-from-CSharp.md</c> "compile once"). Results
/// are parked with <c>rawset</c> (strict-global guard) and read back through the type-strict <c>ILua</c> getters —
/// never a Lua→C# callback (crashes IL2CPP).
/// </summary>
internal static class ChatShareLua
{
    public const string ResultKey = "__stellarChatR";     // preview: "sid|line|x|y|z|layer|can" | "none" | "err" | "nofn"
    public const string NameKey = "__stellarChatN";       // preview: scene name
    public const string TrackKey = "__stellarChatT";      // track: 0 ok, 2 locked, 3 unreachable, -1 threw, -2 no fn
    public const string ShareSentKey = "__stellarChatS";  // share: true once the coroutine was started
    public const string ShareErrKey = "__stellarChatE";   // share: server code (0 = shared), -1 = threw

    // Current position exactly as chat_hyperlink_localposition.lua builds a share (ceil'd server pos, PlayerLineId,
    // MiniMapManager.CurLayerId) + the chat emoji popup's "may share here" rule (static scene, not on
    // Z.Global.CoordShareSceneLimit — the decompiled check is garbled; read as a blocklist, it holds {0}).
    public const string DefinePreview = @"
rawset(_G, '__stellarChatPosFn', function()
  local sid = Z.StageMgr.GetCurrentSceneId()
  local ent = Z.EntityMgr.PlayerEnt
  if sid == nil or sid == 0 or ent == nil then return nil end
  local row = Z.TableMgr.GetTable('SceneTableMgr').GetRow(sid)
  if row == nil then return nil end
  local sl = Z.DataMgr.Get('sceneline_data')
  local line = (sl and sl.PlayerLineId) or 0
  local p = ent:GetLuaAttrPos()
  local i = function(v) return math.floor(tonumber(v) or 0) end
  local can = Z.VMMgr.GetVM('scene').IsStaticScene(sid) and true or false
  local lim = Z.Global.CoordShareSceneLimit
  if can and lim and #lim > 0 and table.zcontains(lim, sid) then can = false end
  return sid, i(line), math.ceil(p.x), math.ceil(p.y), math.ceil(p.z), i(Z.MiniMapManager.CurLayerId), can, row.Name
end)
rawset(_G, '__stellarChatPreviewFn', function()
  local sid, line, x, y, z, layer, can, name = rawget(_G, '__stellarChatPosFn')()
  if sid == nil then return 'none' end
  return string.format('%d|%d|%d|%d|%d|%d|%d', sid, line, x, y, z, layer, can and 1 or 0), name
end)";

    public const string CallPreview = @"
rawset(_G, '__stellarChatR', nil) rawset(_G, '__stellarChatN', nil)
local f = rawget(_G, '__stellarChatPreviewFn')
if f == nil then rawset(_G, '__stellarChatR', 'nofn') return end
local ok, r, n = pcall(f)
if ok then rawset(_G, '__stellarChatR', r) rawset(_G, '__stellarChatN', n) else rawset(_G, '__stellarChatR', 'err') end";

    // WorldProxy.ShareObjectInChat(type Position=8) — what chat_main_vm.AsyncLocalPosition sends, built directly so the
    // player's own chat draft / share state is untouched. Async RPC → must run in a coroutine. Our errFunc REPLACES the
    // game's coroutine error log, so it parks the code instead. On a refusal the game's own tip is shown (as native).
    public const string DefineShare = @"
rawset(_G, '__stellarChatShareFn', function(ch, target, before)
  Z.CoroUtil.create_coro_xpcall(function()
    local sid, line, x, y, z, layer = rawget(_G, '__stellarChatPosFn')()
    if sid == nil then rawset(_G, '__stellarChatE', -1) return end
    local req = {}
    req.channelType = ch
    req.objectType = Z.PbEnum('ShareObjectType', 'ShareObjectTypePosition')
    if before ~= '' then req.beforeDesc = Z.RichTextHelper.RemoveTagsOtherThanEmojis(before) end
    req.targetCharId = target
    req.paramList = {sid, line, x, y, z, layer}
    local err = require('zproxy.world_proxy').ShareObjectInChat(req, ZUtil.ZCancelSource.NeverCancelToken)
    rawset(_G, '__stellarChatE', tonumber(err) or 0)
    if err ~= nil and err ~= 0 then Z.TipsVM.ShowTips(err) end
  end, function(e)
    rawset(_G, '__stellarChatE', type(e) == 'number' and e or -1)
  end)()
end)";

    // chat_main_vm.onClickPositionShare(linkValue): CheckSceneUnlock → minimap.CheckSceneID (else tip 102915) →
    // quick_jump TraceScenePosition (waypoint + map). We pre-check silently to learn WHY, then call the VM's own
    // handler either way so the outcome (incl. the game's tip on a refusal) is exactly a native click's.
    public const string DefineTrack = @"
rawset(_G, '__stellarChatTrackFn', function(sid, line, x, y, z, layer, guid)
  local code = 0
  if not Z.VMMgr.GetVM('scene').CheckSceneUnlock(sid, false) then code = 2
  elseif not Z.VMMgr.GetVM('minimap').CheckSceneID(sid, true) then code = 3 end
  Z.VMMgr.GetVM('chat_main').onClickPositionShare({ value = { SceneId = sid, LineId = line, PositionX = x,
    PositionY = y, PositionZ = z, MapLayerId = layer, SceneGuid = guid } })
  return code
end)";

    /// <summary>One-line share call: parks <see cref="ShareSentKey"/> = true once the coroutine started, false when
    /// the function is missing (re-define), nothing when it threw.</summary>
    public static string CallShare(int channel, long targetCharId, string before)
        => "rawset(_G, '__stellarChatS', nil) rawset(_G, '__stellarChatE', nil) "
         + "local f = rawget(_G, '__stellarChatShareFn') if f == nil then rawset(_G, '__stellarChatS', false) return end "
         + "f(" + Int(channel) + ", " + Int(targetCharId) + ", " + Str(before) + ") rawset(_G, '__stellarChatS', true)";

    /// <summary>One-line track call: parks the code in <see cref="TrackKey"/> (-2 when the function is missing).</summary>
    public static string CallTrack(int sid, int line, (int X, int Y, int Z) pos, int layer, string? guid)
        => "rawset(_G, '__stellarChatT', nil) local f = rawget(_G, '__stellarChatTrackFn') "
         + "if f == nil then rawset(_G, '__stellarChatT', -2) return end "
         + "local ok, r = pcall(f, " + Int(sid) + ", " + Int(line) + ", " + Int(pos.X) + ", " + Int(pos.Y) + ", "
         + Int(pos.Z) + ", " + Int(layer) + ", " + (guid is null ? "nil" : Str(guid)) + ") "
         + "rawset(_G, '__stellarChatT', ok and r or -1)";

    private static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A Lua double-quoted literal: backslash / quote escaped, control chars as <c>\ddd</c> (so a pasted
    /// newline can't break the chunk). Other characters pass through (the chunk is UTF-8 on the Lua side).</summary>
    internal static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            if (c == '\\') sb.Append("\\\\");
            else if (c == '"') sb.Append("\\\"");
            else if (c < 0x20 || c == 0x7F) sb.Append('\\').Append(((int)c).ToString("D3", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
