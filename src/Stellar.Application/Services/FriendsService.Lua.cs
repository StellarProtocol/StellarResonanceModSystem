namespace Stellar.Application.Services;

internal sealed partial class FriendsService
{
    // __stlr_fr_poll(): walks the game's own data and parks two strings (rawset — _G is strict-guarded):
    //   __stlr_fr_f  friends   rec = cid \31 name \31 remark \31 level \31 professionId \31 groupId \31 online(1/0) \31 avatarUrl
    //                (friend_main_data:GetFriendDic() = groupAndFriendList_[groupId][charId] = FriendData;
    //                 Shield group (99, blocked) skipped like GetOnlineFriendList; rows sorted → stable string)
    //   __stlr_fr_p  private   rec = cid \31 name \31 isTop \31 unread \31 text \31 msgType \31 senderId \31 ts \31 online(1/0/'')
    //                (chat_main_data:GetPrivateChatList() in the game's display order; name/online from the item's
    //                 socialData.basicData, else the friend entry, else the latest message's sender when it's the peer;
    //                 unread = latestMsg.msgId − maxReadMsgId exactly as GetPrivateChatUnReadCount)
    // Records \30-separated. Each row is its own pcall (one bad row never blanks the list). Ids/numbers via '%.0f'
    // (exact to 2^53) or tostring for tolua int64 userdata. __stlr_fr_ok = true is set FIRST = "walker installed";
    // each list section is its own pcall and only overwrites its string on success, so a data table that is briefly
    // unavailable leaves the last good string (= "unchanged" to C#) instead of forcing a reinstall loop.
    private const string InstallChunk = @"pcall(function()
  local function s(v)
    if v == nil then return '' end
    if type(v) == 'number' then
      if v == math.floor(v) then return string.format('%.0f', v) end
      return tostring(v)
    end
    if type(v) == 'boolean' then return v and '1' or '0' end
    return (string.gsub(tostring(v), '[\30\31]', ' '))
  end
  local function call(o, m) local f = o and o[m] if f then local ok, v = pcall(f, o) if ok then return v end end return nil end
  rawset(_G, '__stlr_fr_poll', function()
    rawset(_G, '__stlr_fr_ok', true)
    local okD, fmd = pcall(function() return Z.DataMgr.Get('friend_main_data') end)
    if not okD then fmd = nil end
    pcall(function()
    local rows = {}
    local okS, shield = pcall(function() return E.FriendGroupType.Shield end)
    if not okS then shield = 99 end
    local dic = fmd and call(fmd, 'GetFriendDic')
    if type(dic) == 'table' then
      for gid, list in pairs(dic) do
        if gid ~= shield and type(list) == 'table' then
          for cid, fd in pairs(list) do
            pcall(function()
              local sd = call(fd, 'GetSocialData')
              local prof = sd and sd.professionData and sd.professionData.professionId
              local pic = call(fd, 'GetPlayerProfileInfo')
              rows[#rows + 1] = s(call(fd, 'GetCharId') or cid) .. '\31' .. s(call(fd, 'GetPlayerName')) .. '\31'
                .. s(call(fd, 'GetRemark')) .. '\31' .. s(call(fd, 'GetPlayerLevel')) .. '\31' .. s(prof) .. '\31'
                .. s(gid) .. '\31' .. ((call(fd, 'GetPlayerOffLineTime') == 0) and '1' or '0') .. '\31'
                .. s(type(pic) == 'table' and pic.url or nil)
            end)
          end
        end
      end
    end
    table.sort(rows)
    rawset(_G, '__stlr_fr_f', table.concat(rows, '\30'))
    end)
    pcall(function()
    local cmd = Z.DataMgr.Get('chat_main_data')
    local list = cmd and call(cmd, 'GetPrivateChatList')
    local prow = {}
    if type(list) == 'table' then
      for i = 1, #list do
        pcall(function()
          local it = list[i]
          local cid = it.charId
          local name, online = nil, ''
          local b = it.socialData and it.socialData.basicData
          if b then name = b.name if b.offlineTime ~= nil then online = (b.offlineTime == 0) and '1' or '0' end end
          if (name == nil or name == '') and fmd then
            local okF, fd = pcall(fmd.GetFriendDataByCharId, fmd, cid)
            if okF and fd then name = call(fd, 'GetPlayerName') end
          end
          local lm = it.latestMsg
          local text, mtype, sender, ts, unread = '', -1, nil, nil, 0
          if lm then
            ts = lm.timestamp
            if lm.msgInfo then text = lm.msgInfo.msgText mtype = lm.msgInfo.msgType or 0 end
            local sc = lm.sendCharInfo
            if sc then
              sender = sc.charID
              if (name == nil or name == '') and tostring(sender) == tostring(cid) then name = sc.name end
            end
            pcall(function()
              if lm.msgId and it.maxReadMsgId and it.maxReadMsgId < lm.msgId then unread = lm.msgId - it.maxReadMsgId end
            end)
          end
          prow[#prow + 1] = s(cid) .. '\31' .. s(name) .. '\31' .. (it.isTop and '1' or '0') .. '\31' .. s(unread)
            .. '\31' .. s(text) .. '\31' .. s(mtype) .. '\31' .. s(sender) .. '\31' .. s(ts) .. '\31' .. online
        end)
      end
    end
    rawset(_G, '__stlr_fr_p', table.concat(prow, '\30'))
    end)
  end)
end)";

    // Per-poll chunk: clear the installed flag first, so a missing walker reads back "not ok" (→ install).
    private const string PollChunk =
        "rawset(_G,'__stlr_fr_ok',nil) local f=rawget(_G,'__stlr_fr_poll') if f then pcall(f) end";

    private const string OkGlobal = "__stlr_fr_ok";
    private const string FriendsGlobal = "__stlr_fr_f";
    private const string PrivateGlobal = "__stlr_fr_p";

    // Actions — the game's own coroutine-only VM calls (Async* yield → must run inside Z.CoroUtil.create_coro_xpcall).
    // Chunk = Prologue + charId literal + " " + Body + Epilogue (concatenated, not string.Format). Mirrors ui/service/chat_service.lua createPrivateChat / deletePrivateChat and the friends
    // window's read path (AsyncSetPrivateChatHasRead(charId, latestMsg.msgId, token)). Optional UI refreshes last, so
    // an error there can't skip the action itself. Fire-and-forget; errors swallowed by the xpcall handler.
    private const string ActionPrologue =
        "pcall(function() (Z.CoroUtil).create_coro_xpcall(function() local vm = (Z.VMMgr).GetVM('chat_main') " +
        "local d = (Z.DataMgr).Get('chat_main_data') local cid = ";
    private const string ActionEpilogue = " end, function() end)() end)";

    private const string HistoryBody = "vm.AsyncGetPrivateChatRecord(cid, 0, true)";

    private const string CreateBody =
        "vm.AsyncCreatePrivateChat(cid, d.CancelSource:CreateToken()) vm.AsyncUpdatePrivateChatCharInfo() " +
        "vm.AsyncGetPrivateChatRecord(cid, 0, true) d:SortPrivateChatList() " +
        "pcall(vm.CheckMainUIFriendNewMessage) (Z.EventMgr):Dispatch(Z.ConstValue.Chat.PrivateChatRefresh)";

    // Mirrors friend_chat_item_pc.lua setRead: the RPC (ChitChat method 6) runs ONLY while something is unread
    // (maxReadMsgId < latestMsg.msgId, both read NOW, at run time) — the server write is skipped entirely otherwise, which
    // also keeps a failing RPC (codes < 1000 make the proxy Disconnect) as rare as possible. The VM does NOT refresh any
    // badge itself: on success the caller must update the FriendChatTab red dot (3rd arg true = keep its shown state, as
    // the native caller does), the HUD friend-message badge (CheckMainUIFriendNewMessage) and the native conversation rows
    // (PrivateChatRefresh). Each refresh is its own pcall so one failing can't skip the others.
    private const string ReadBody =
        "local it = d:GetPrivateChatItemByCharId(cid) local lm = it and it.latestMsg " +
        "if lm and lm.msgId and (it.maxReadMsgId or 0) < lm.msgId then " +
        "if vm.AsyncSetPrivateChatHasRead(cid, lm.msgId, d.CancelSource:CreateToken()) then " +
        "pcall(function() (Z.RedPointMgr).UpdateNodeCount(E.RedType.FriendChatTab, d:GetPrivateChatUnReadCount(), true) end) " +
        "pcall(vm.CheckMainUIFriendNewMessage) " +
        "pcall(function() (Z.EventMgr):Dispatch(Z.ConstValue.Chat.PrivateChatRefresh) end) end end";

    private const string CloseBody =
        "vm.AsyncDeletePrivateChat(cid, d.CancelSource:CreateToken()) " +
        "pcall(function() (Z.RedPointMgr).UpdateNodeCount(E.RedType.FriendChatTab, d:GetPrivateChatUnReadCount()) end) " +
        "pcall(vm.CheckMainUIFriendNewMessage) (Z.EventMgr):Dispatch(Z.ConstValue.Chat.PrivateChatRefresh)";
}
