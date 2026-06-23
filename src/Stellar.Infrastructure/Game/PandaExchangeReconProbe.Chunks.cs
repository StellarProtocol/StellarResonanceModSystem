namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeReconProbe
{
    // PASS 1 — enumerate candidate exchange VMs and dump their function tables.
    // Swap this constant for the query/buy chunk (Task 8) before Pass 2.
    private const string ReconChunk =
        "local cands={\"exchange\",\"trade\",\"market\",\"auction\",\"shop\",\"mall\",\"store\",\"marketplace\",\"tradingpost\"}" +
        " local out=\"\"" +
        " for _,n in ipairs(cands) do" +
        "  local ok,vm=pcall(function() return Z.VMMgr.GetVM(n) end)" +
        "  if ok and vm~=nil then" +
        "   out=out..\"VM=\"..n..\"\\n\"" +
        "   local seen={}" +
        "   for k,v in pairs(vm) do if type(v)==\"function\" then out=out..\"  .\"..tostring(k)..\"\\n\" seen[k]=true end end" +
        "   local mt=getmetatable(vm)" +
        "   if mt and mt.__index and type(mt.__index)==\"table\" then" +
        "    for k,v in pairs(mt.__index) do if type(v)==\"function\" and not seen[k] then out=out..\"  mt.\"..tostring(k)..\"\\n\" end end" +
        "   end" +
        "  else out=out..\"VM=\"..n..\" <nil>\\n\" end" +
        " end" +
        " rawset(_G,\"" + ReconGlobal + "\", out)";
}
