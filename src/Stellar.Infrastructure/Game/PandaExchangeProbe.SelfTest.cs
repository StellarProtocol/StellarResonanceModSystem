using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

// RECON-style self-test (Phase 1, Task 6 Step 6). Flag-gated (EXCHANGE_SELFTEST), fires ONCE in-world,
// dumps the trade VM query return/model structure so QueryListings/Notice + result-poll can be wired.
// Remove this partial before merge (or once the model is wired).
internal sealed partial class PandaExchangeProbe
{
    private bool _selfTestFired;
    private string? _selfTestLast;

    // Plain const (no string.Format) — literal Lua braces are fine. seq hardcoded 0 to test viability.
    private const string SelfTestChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then return end" +
        " local out={} local function L(s) out[#out+1]=tostring(s) end" +
        " local care=vm:AsyncExchangeCareList(1, 0)" +
        " L(\"CARE type=\"..type(care))" +
        " if type(care)==\"table\" then local n=0" +
        "  for k,v in pairs(care) do n=n+1 if n<=3 then L(\" care[\"..tostring(k)..\"] type=\"..type(v))" +
        "   if type(v)==\"table\" then for fk,fv in pairs(v) do L(\"    .\"..tostring(fk)..\"=\"..type(fv)..\"(\"..tostring(fv)..\")\") end end end end" +
        "  L(\" care count=\"..n) end" +
        " local lr=vm:AsyncExchangeList(1, 101, 0)" +
        " L(\"LIST ret=\"..type(lr)..\"(\"..tostring(lr)..\")\")" +
        " for _,nm in ipairs({\"trade_data\",\"trade\",\"exchange_data\",\"exchange\",\"tradeData\",\"exchangeData\"}) do" +
        "  local ok,d=pcall(function() return Z.DataMgr.Get(nm) end)" +
        "  if ok and d~=nil then L(\"MODEL \"..nm..\" type=\"..type(d))" +
        "   if type(d)==\"table\" then local c=0 for fk,fv in pairs(d) do c=c+1 if c<=40 then L(\"   \"..nm..\".\"..tostring(fk)..\"=\"..type(fv)) end end end end end" +
        " rawset(_G,\"" + ResultGlobal + "\", table.concat(out,\"\\n\"))" +
        " end))()";

    // Call from DrainPendingDispatches (after the bridge is resolved). No-op unless the flag is set.
    private void MaybeRunSelfTest()
    {
        if (!PerfControls.Flag("EXCHANGE_SELFTEST")) return;
        if (!_selfTestFired) { _selfTestFired = true; InvokeChunk(SelfTestChunk); }
        var dump = ReadLuaGlobalString(ResultGlobal);
        if (dump is null || dump == _selfTestLast) return;
        _selfTestLast = dump;
        foreach (var line in dump.Split('\n'))
            if (line.Length > 0) _log.Info("[Stellar][Exchange][selftest] " + line);
    }
}
