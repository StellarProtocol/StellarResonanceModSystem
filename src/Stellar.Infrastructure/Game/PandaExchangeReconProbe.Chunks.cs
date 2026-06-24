namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeReconProbe
{
    // PASS 2b (fixed) — install logging shims on the `trade` VM fns to capture the EXACT args the GAME
    // passes when the player uses the trade UI (arg order + return shape — Q2/Q3). _G is write-protected
    // by the game, so ALL global access uses rawget/rawset (cf. PandaLoadoutProbe). The shim is installed
    // via rawset(vm,name,...) and the original is always called (o(...) is NOT pcall'd — async fns yield);
    // only the logging is pcall-guarded so it can never block a real trade action. Idempotent via
    // _SXW_INSTALLED so re-firing on the tick is a no-op.
    private const string WrapInstallChunk =
        "if rawget(_G,\"_SXW_INSTALLED\") then return end" +
        " if Z==nil or Z.VMMgr==nil or Z.VMMgr.GetVM==nil then return end" +
        " local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then return end" +
        " rawset(_G,\"_SXW\",{})" +
        " local function rec(s) local t=rawget(_G,\"_SXW\") if t==nil then t={} rawset(_G,\"_SXW\",t) end" +
        "  t[#t+1]=tostring(s) rawset(_G,\"" + ReconGlobal + "\", table.concat(t,\"\\n\")) end" +
        " local function wrap(name) local o=vm[name]" +
        "  if type(o)~=\"function\" then rec(\"WRAP \"..name..\" MISSING\") return end" +
        "  rawset(vm,name,function(...) local n=select(\"#\",...) local a={...}" +
        "   pcall(function() local p={} for i=1,n do p[i]=\"a\"..i..\"=\"..type(a[i])..\"(\"..tostring(a[i])..\")\" end" +
        "    rec(\"CALL \"..name..\" [\"..n..\"] \"..table.concat(p,\" \")) end)" +
        "   local rv=o(...)" +
        "   pcall(function() rec(\"RET  \"..name..\" -> \"..type(rv)..\"(\"..tostring(rv)..\")\") end)" +
        "   return rv end)" +
        "  rec(\"WRAP \"..name..\" ok\") end" +
        " local names={\"AsyncExchangeBuyItem\",\"AsyncExchangeList\",\"AsyncExchangeItem\",\"AsyncExchangeLowestPrice\",\"AsyncExchangeCareList\",\"AsyncExchangeNoticeDetail\",\"AsyncExchangeNoticeBuyItem\",\"CheckItemIsPreOrder\",\"CheckItemCanExchange\",\"CheckPreOrderMaxNum\"}" +
        " for _,nm in ipairs(names) do wrap(nm) end" +
        " rawset(_G,\"_SXW_INSTALLED\",true)" +
        " rec(\"=== wrappers installed; use the trade UI now ===\")";
}
