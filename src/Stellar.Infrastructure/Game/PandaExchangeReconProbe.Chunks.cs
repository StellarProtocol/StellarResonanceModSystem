namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeReconProbe
{
    // PASS 2b — install logging shims on the `trade` VM fns to capture the EXACT args the GAME passes
    // when the player uses the trade UI (definitive arg-order discovery + return shape — Q2/Q3).
    // Idempotent via _SXW_INSTALLED so re-firing on the tick is a no-op. Each shim calls the original
    // transparently (`return o(...)`) so the game behaves normally; shims are NOT pcall'd (async fns
    // yield — recon/loadout-switch-findings.md:308). Accumulates into _SXW + mirrors to the recon global.
    private const string WrapInstallChunk =
        "if _G._SXW_INSTALLED then return end" +
        " if Z==nil or Z.VMMgr==nil or Z.VMMgr.GetVM==nil then return end" +
        " local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then return end" +
        " _G._SXW={}" +
        " local function rec(s) local t=_G._SXW t[#t+1]=tostring(s) rawset(_G,\"" + ReconGlobal + "\", table.concat(t,\"\\n\")) end" +
        " local function argstr(...) local a={...} local n=select(\"#\",...) local p={}" +
        "  for i=1,n do p[i]=\"a\"..i..\"=\"..type(a[i])..\"(\"..tostring(a[i])..\")\" end" +
        "  return \"[\"..n..\"] \"..table.concat(p,\" \") end" +
        " local function wrap(name) local o=vm[name]" +
        "  if type(o)~=\"function\" then rec(\"WRAP \"..name..\" MISSING\") return end" +
        "  vm[name]=function(...) rec(\"CALL \"..name..\" \"..argstr(...))" +
        "   local rv=o(...) rec(\"RET  \"..name..\" -> \"..type(rv)..\"(\"..tostring(rv)..\")\") return rv end" +
        "  rec(\"WRAP \"..name..\" ok\") end" +
        " local names={\"AsyncExchangeBuyItem\",\"AsyncExchangeList\",\"AsyncExchangeItem\",\"AsyncExchangeLowestPrice\",\"AsyncExchangeCareList\",\"AsyncExchangeNoticeDetail\",\"AsyncExchangeNoticeBuyItem\",\"CheckItemIsPreOrder\",\"CheckItemCanExchange\",\"CheckPreOrderMaxNum\"}" +
        " for _,n in ipairs(names) do wrap(n) end" +
        " _G._SXW_INSTALLED=true" +
        " rec(\"=== wrappers installed; use the trade UI now ===\")";
}
