using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

// Self-test (Phase 1, Task 6 Step 6 discovery). Flag-gated (EXCHANGE_SELFTEST). Re-fires on a timer.
// Sync model-probe runs first (captured even if the async call hangs); then a non-zero-seq async
// care-list with a marker before it. Remove this partial before merge.
internal sealed partial class PandaExchangeProbe
{
    private int _selfTestTick;
    private const int SelfTestEveryTicks = 150;   // ~5s — retries after the user opens the trade UI
    private string? _selfTestLast;

    private const string SelfTestChunk =
        "local out={} local function L(s) out[#out+1]=tostring(s) rawset(_G,\"" + ResultGlobal + "\", table.concat(out,\"\\n\")) end" +
        " if Z==nil or Z.VMMgr==nil then L(\"Z/VMMgr nil\") return end" +
        " local vm=Z.VMMgr.GetVM(\"trade\") if vm==nil then L(\"trade VM nil\") return end" +
        " L(\"=== selftest begin ===\")" +
        " for _,nm in ipairs({\"trade_data\",\"trade\",\"exchange_data\",\"exchange\",\"tradeData\",\"exchangeData\"}) do" +
        "  local ok,d=pcall(function() return Z.DataMgr.Get(nm) end)" +
        "  if ok and d~=nil then L(\"MODEL \"..nm..\" type=\"..type(d))" +
        "   if type(d)==\"table\" then local c=0 for fk,fv in pairs(d) do c=c+1 if c<=50 then L(\"  \"..nm..\".\"..tostring(fk)..\"=\"..type(fv)) end end end end end" +
        " local seq=(rawget(_G,\"_sxseq\") or 990000)+1 rawset(_G,\"_sxseq\",seq)" +
        " L(\"about to AsyncExchangeCareList(1, \"..seq..\")\")" +
        " ;(Z.CoroUtil.create_coro_xpcall(function()" +
        "  local care=vm:AsyncExchangeCareList(1, seq)" +
        "  L(\"CARE returned type=\"..type(care))" +
        "  if type(care)==\"table\" then local n=0 for k,v in pairs(care) do n=n+1 if n<=3 then L(\" care elem[\"..tostring(k)..\"] type=\"..type(v))" +
        "   if type(v)==\"table\" then for fk,fv in pairs(v) do L(\"   .\"..tostring(fk)..\"=\"..type(fv)..\"(\"..tostring(fv)..\")\") end end end end L(\" care count=\"..n) end" +
        " end))()";

    private void MaybeRunSelfTest()
    {
        if (!PerfControls.Flag("EXCHANGE_SELFTEST")) return;
        if (_selfTestTick++ % SelfTestEveryTicks == 0) InvokeChunk(SelfTestChunk);
        var dump = ReadLuaGlobalString(ResultGlobal);
        if (dump is null || dump == _selfTestLast) return;
        _selfTestLast = dump;
        foreach (var line in dump.Split('\n'))
            if (line.Length > 0) _log.Info("[Stellar][Exchange][selftest] " + line);
    }
}
