namespace Stellar.Application.Services;

internal sealed partial class SystemNoticeService
{
    // Globals (all written with rawset — the game's _G strict guard silently drops plain global writes):
    //   __stlr_sys_push   function(info): formats + queues one notice (looked up by the wrapper at CALL time, so a
    //                     reinstall swaps the formatter without re-wrapping)
    //   __stlr_sys_drain  function(): (re)wraps the VM method if needed, then parks "<gen>\30<rec>\30<rec>…" in
    //                     __stlr_sys_out and empties the queue
    //   __stlr_sys_q      the queue (array of preformatted "type\31id\31label\31content" strings, capped at 64)
    //   __stlr_sys_gen    wrap generation — bumps each time the wrap is (re)applied (VM table/function replaced)
    //   __stlr_sys_on     capture flag (set by InstallChunk / ResumeChunk, cleared by IdleChunk): the push is a no-op
    //                     while it is nil, so with no chat consumer a notice costs the wrapper one table read
    //   __stlr_sys_busy   re-entrancy flag: if anything ever wraps over our wrapper and we wrap again, only the
    //                     OUTERMOST of our wrappers pushes, so a notice is never queued twice
    // The wrap marker lives ON the VM table (rawset(vm,'__stlr_sys_w',w)), so if our globals are ever lost the
    // still-installed wrapper is recognised and not double-wrapped; a reloaded VM table has no marker → re-wrap.
    // Record fields are \31-separated, records \30-separated; both bytes are scrubbed out of the strings.
    // Numbers go through '%.0f' (exact to 2^53; '%d' is 32-bit `long` on a Windows Lua 5.1 build).
    // NEVER calls C# — the C# side polls __stlr_sys_out (a native Lua→C# callback crashes the IL2CPP runtime).
    private const string InstallChunk = @"pcall(function()
  local function s(v)
    if v == nil then return '' end
    if type(v) == 'number' then
      if v == math.floor(v) then return string.format('%.0f', v) end
      return tostring(v)
    end
    return (string.gsub(tostring(v), '[\30\31]', ' '))
  end
  rawset(_G, '__stlr_sys_on', true)
  rawset(_G, '__stlr_sys_push', function(info)
    if type(info) ~= 'table' or not rawget(_G, '__stlr_sys_on') then return end
    local q = rawget(_G, '__stlr_sys_q')
    if not q then q = {} rawset(_G, '__stlr_sys_q', q) end
    if #q >= 64 then table.remove(q, 1) end
    local rec = { ChannelId = 99, SystemType = info.Type, SystemId = info.Id, SystemContent = info.Content,
                  HeadStr = info.HeadStr, MsgText = info.Content }
    local t, id, hs, content = rec.SystemType, rec.SystemId, rec.HeadStr, rec.SystemContent
    local okf, a, b, c, d = pcall(Z.ChatMsgHelper.GetSystemType, rec)
    if okf then t, id, hs, content = a, b, c, d end
    local okE, itemType = pcall(function() return E.ESystemTipInfoType.ItemInfo end)
    if not okE then itemType = 1 end
    if t == itemType then
      if hs == nil or hs == '' then hs = rec.HeadStr end
      if hs == nil or hs == '' then local okL, l = pcall(Lang, 'Acquire') if okL then hs = l end end
      if content == nil or content == '' then content = rec.SystemContent end
    else
      hs = rec.HeadStr
      content = rec.SystemContent
    end
    q[#q + 1] = s(t) .. '\31' .. s(id) .. '\31' .. s(hs) .. '\31' .. s(content)
  end)
  rawset(_G, '__stlr_sys_drain', function()
    pcall(function()
      local vm = Z.VMMgr.GetVM('chat_main')
      if not vm then return end
      local cur = vm.SetReceiveSystemMsg
      if cur == nil or rawget(vm, '__stlr_sys_w') == cur then return end
      local orig = cur
      local unpack = rawget(_G, 'unpack') or table.unpack
      local function pack(...) return select('#', ...), { ... } end
      local w
      w = function(info, ...)
        if rawget(_G, '__stlr_sys_busy') then return orig(info, ...) end
        rawset(_G, '__stlr_sys_busy', true)
        local ok, n, r = pcall(function(...) return pack(orig(...)) end, info, ...)
        rawset(_G, '__stlr_sys_busy', nil)
        if not ok then error(n, 0) end
        local push = rawget(_G, '__stlr_sys_push')
        if push then pcall(push, info) end
        return unpack(r, 1, n)
      end
      rawset(vm, '__stlr_sys_w', w)
      vm.SetReceiveSystemMsg = w
      rawset(_G, '__stlr_sys_gen', (rawget(_G, '__stlr_sys_gen') or 0) + 1)
    end)
    local out = s(rawget(_G, '__stlr_sys_gen') or 0)
    local q = rawget(_G, '__stlr_sys_q')
    if q and #q > 0 then
      out = out .. '\30' .. table.concat(q, '\30')
      rawset(_G, '__stlr_sys_q', {})
    end
    rawset(_G, '__stlr_sys_out', out)
  end)
end)";

    // Per-poll chunk (tiny — compiled each poll). Clears the out-slot FIRST so a missing drain function reads back
    // nil (= not installed → C# runs InstallChunk) instead of a stale batch.
    private const string PollChunk =
        "rawset(_G,'__stlr_sys_out',nil) local f=rawget(_G,'__stlr_sys_drain') if f then pcall(f) end";

    private const string OutGlobal = "__stlr_sys_out";

    // Lazy capture (SystemNoticeService.Tick): no chat consumer → the push returns at once (the wrapper stays installed as a
    // pass-through) and the queue is dropped; a consumer again → the flag comes back up with an empty queue.
    private const string IdleChunk = "rawset(_G,'__stlr_sys_on',nil) rawset(_G,'__stlr_sys_q',nil)";
    private const string ResumeChunk = "rawset(_G,'__stlr_sys_q',nil) rawset(_G,'__stlr_sys_on',true)";
}
