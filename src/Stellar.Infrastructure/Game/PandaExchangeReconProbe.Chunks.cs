namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeReconProbe
{
    // PASS 1 — dump the VM registry via debug.getupvalue(Z.VMMgr.GetVM,i) (VMs live in
    // GetVM's upvalue closure, NOT reachable by walking Z — recon/loadout-switch-findings.md:311),
    // then enumerate functions of any exchange-like VM key. Non-yielding → no coroutine.
    // Swap this constant for the query/buy chunk (Task 8) before Pass 2.
    private const string ReconChunk =
        "local lines={} local function L(s) lines[#lines+1]=tostring(s) end" +
        " local function run()" +
        "  if Z==nil or Z.VMMgr==nil or Z.VMMgr.GetVM==nil then L(\"Z/VMMgr/GetVM nil - fired too early\") return end" +
        "  if not debug or not debug.getupvalue then L(\"no debug.getupvalue\") return end" +
        "  local keys={} local i=1" +
        "  while true do local n,v=debug.getupvalue(Z.VMMgr.GetVM, i) if not n then break end" +
        "   if type(v)==\"table\" then local cnt=0 for k,_ in pairs(v) do cnt=cnt+1 keys[#keys+1]=tostring(k) end" +
        "    L(\"up[\"..i..\"] \"..tostring(n)..\" (#keys=\"..cnt..\")\") end" +
        "   i=i+1 end" +
        "  table.sort(keys) for _,k in ipairs(keys) do L(\"VMKEY \"..k) end" +
        "  local pat={\"exchang\",\"trade\",\"market\",\"auction\",\"shop\",\"mall\",\"store\",\"sale\",\"deal\",\"vend\"}" +
        "  for _,k in ipairs(keys) do local lk=string.lower(k) local m=false" +
        "   for _,p in ipairs(pat) do if string.find(lk,p,1,true) then m=true break end end" +
        "   if m then local ok,vm=pcall(function() return Z.VMMgr.GetVM(k) end)" +
        "    if ok and vm~=nil then L(\"== VM \"..k..\" functions ==\")" +
        "     local fns={} for fk,fv in pairs(vm) do if type(fv)==\"function\" then fns[#fns+1]=tostring(fk) end end" +
        "     table.sort(fns) for _,f in ipairs(fns) do L(\"  fn \"..f) end end end end" +
        " end" +
        " pcall(run)" +
        " rawset(_G,\"" + ReconGlobal + "\", table.concat(lines,\"\\n\"))";
}
