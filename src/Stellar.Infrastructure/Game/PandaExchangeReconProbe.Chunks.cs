using System.Globalization;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeReconProbe
{
    // PASS 2 — exercise the discovered `trade` VM (item id = Pass-1 bot target).
    // (1) synchronous introspection: nparams/arity of the key fns + CheckItemIsPreOrder/CanExchange
    //     (Q2/Q5), written to the global immediately so it lands even if the async query sig is off.
    // (2) coroutine read-only query AsyncExchangeLowestPrice (Q3/Q4-read), appended to the global.
    // Async calls run in create_coro_xpcall with NeverCancelToken and are NOT wrapped in pcall
    // (LuaJIT pcall can't yield) — recon/loadout-switch-findings.md:308-311.
    private static string BuildQueryChunk(int itemId)
        => string.Format(CultureInfo.InvariantCulture,
            "local lines={{}} local function L(s) lines[#lines+1]=tostring(s) end" +
            " local function introspect()" +
            "  if Z==nil or Z.VMMgr==nil or Z.VMMgr.GetVM==nil then L(\"Z/VMMgr nil\") return end" +
            "  local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then L(\"trade VM nil\") return end" +
            "  local function ar(n) local f=vm[n] if type(f)~=\"function\" then L(n..\" MISSING\") return end" +
            "   local ok,info=pcall(debug.getinfo, f, \"u\") if ok and info then L(n..\" nparams=\"..tostring(info.nparams)..\" vararg=\"..tostring(info.isvararg)) else L(n..\" getinfo-fail\") end end" +
            "  ar(\"AsyncExchangeBuyItem\") ar(\"AsyncExchangeList\") ar(\"AsyncExchangeItem\") ar(\"AsyncExchangeLowestPrice\")" +
            "  ar(\"AsyncExchangeCareList\") ar(\"AsyncExchangeNoticeDetail\") ar(\"AsyncExchangeNoticeBuyItem\")" +
            "  ar(\"CheckItemIsPreOrder\") ar(\"CheckPreOrderMaxNum\") ar(\"CheckItemCanExchange\")" +
            "  local okp,pre=pcall(function() return vm.CheckItemIsPreOrder({0}) end) L(\"CheckItemIsPreOrder({0}) ok=\"..tostring(okp)..\" -> \"..tostring(pre))" +
            "  local okc,ce=pcall(function() return vm.CheckItemCanExchange({0}) end) L(\"CheckItemCanExchange({0}) ok=\"..tostring(okc)..\" -> \"..tostring(ce))" +
            " end" +
            " pcall(introspect)" +
            " rawset(_G,\"{1}\", table.concat(lines,\"\\n\"))" +
            " ;(Z.CoroUtil.create_coro_xpcall(function()" +
            "  local token=(ZUtil.ZCancelSource).NeverCancelToken" +
            "  local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then return end" +
            "  local q={{}}" +
            "  local r=vm.AsyncExchangeLowestPrice({0}, token)" +
            "  q[#q+1]=\"AsyncExchangeLowestPrice ret=\"..type(r)..\" \"..tostring(r)" +
            "  if type(r)==\"table\" then local c=0 for k,v in pairs(r) do c=c+1 if c<=30 then q[#q+1]=\"  \"..tostring(k)..\"=\"..type(v)..\" \"..tostring(v) end end q[#q+1]=\"  (#keys=\"..c..\")\" end" +
            "  local cur=rawget(_G,\"{1}\") or \"\"" +
            "  rawset(_G,\"{1}\", cur..\"\\n--- async query ---\\n\"..table.concat(q,\"\\n\"))" +
            " end))()",
            itemId, ReconGlobal);

    // PASS 2 buy — one-shot. Fetches the actual cheapest listing price and buys at THAT exact price
    // only if <= ceiling (mirrors the bot — avoids price-mismatch rejects / overpaying). Drives the
    // game's OWN trade VM buy wrapper; the server validates.
    private static string BuildBuyChunk(int itemId, long ceiling)
        => string.Format(CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function()" +
            " local token=(ZUtil.ZCancelSource).NeverCancelToken" +
            " local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then return end" +
            " local out=\"--- BUY ---\"" +
            " local r=vm.AsyncExchangeLowestPrice({0}, token)" +
            " local price=nil" +
            " if type(r)==\"number\" then price=r elseif type(r)==\"table\" then price=r.price or r.Price or r.lowestPrice or r.LowestPrice or r.min_price end" +
            " out=out..\"\\nlowest=\"..tostring(price)" +
            " if price~=nil and price<={1} then" +
            "  local ok=vm.AsyncExchangeBuyItem({0}, 1, price, token)" +
            "  out=out..\"\\nAsyncExchangeBuyItem({0},1,\"..tostring(price)..\") -> \"..type(ok)..\" \"..tostring(ok)" +
            " else out=out..\"\\nskip buy (price nil or > ceiling {1})\" end" +
            " rawset(_G,\"{2}\", out)" +
            " end))()",
            itemId, ceiling, ReconGlobal);
}
